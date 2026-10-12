using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Models;

/// <summary>How the store behaves. <see cref="DownloadsAllowed"/> is the show gate: while it answers false
/// (a show is running) every install is refused — downloads happen only when the engine asks between shows.</summary>
public sealed record ModelStoreOptions(
    Func<bool>? DownloadsAllowed = null,
    long MaxBytesPerFile = 8L << 30,
    TimeSpan? StallTimeout = null,
    /// <summary>Plain HTTP to a loopback address (a local mirror, the tests); HTTPS is required elsewhere.</summary>
    bool AllowLoopbackHttp = true);

public enum ModelInstallStatus
{
    Installed,
    AlreadyInstalled,
    /// <summary>Not attempted, with the reason (licence, show, unverified, gated).</summary>
    Refused,
    /// <summary>Attempted and failed (network, checksum); a partial download is kept for resuming.</summary>
    Failed,
}

public sealed record ModelInstallResult(string ModelId, ModelInstallStatus Status, string Words, long BytesDownloaded)
{
    public bool IsInstalled => Status is ModelInstallStatus.Installed or ModelInstallStatus.AlreadyInstalled;
}

public readonly record struct ModelDownloadProgress(string ModelId, string FileName, long Received, long Total);

public sealed record InstalledFile(string Role, string Path, string Sha256, long SizeBytes);

public sealed record InstalledModel(string Id, string Directory, string LicenceId, DateTime InstalledUtc, ImmutableArray<InstalledFile> Files)
{
    public string? PathOf(string role) => Files.FirstOrDefault(f => f.Role == role)?.Path;
}

/// <summary>An operator's acceptance of a model's licence, as recorded.</summary>
public sealed record LicenceAcceptance(string ModelId, string LicenceId, string AcceptedBy, DateTime AcceptedUtc, bool NonCommercialAcknowledged);

/// <summary>The answer to an acceptance: recorded, or refused with the reason.</summary>
public sealed record LicenceDecision(bool Accepted, string Words);

/// <summary>Models on disk under the node's data folder (<c>&lt;data&gt;/models/&lt;id&gt;/</c>): installs on
/// demand over HTTPS with resume (a <c>.partial</c> file and an HTTP range request) and SHA-256 verification,
/// refuses a model whose licence the operator has not accepted (a non-commercial one needs that said out loud,
/// a gated one needs the publisher's token), and lists what is installed. One install at a time per store. An
/// access token rides only in the request's Authorization header — never in words, files or logs.</summary>
public sealed class ModelStore : IDisposable
{
    private const string ManifestName = "install.json";
    private const string LicencesName = "licences.json";

    private readonly ModelStoreOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly Lock _licenceLock = new();

    public ModelStore(string dataFolder, ModelCatalog catalog, ModelStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataFolder);
        ArgumentNullException.ThrowIfNull(catalog);
        Root = Path.GetFullPath(Path.Combine(dataFolder, "models"));
        Directory.CreateDirectory(Root);
        Catalog = catalog;
        _options = options ?? new ModelStoreOptions();
        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Root { get; }
    public ModelCatalog Catalog { get; }

    /// <summary>Ids are lower-case words with dots, dashes and underscores; file names are plain names. Both
    /// become paths, so nothing else is allowed in.</summary>
    internal static void ValidateEntry(VisionModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsSafeName(model.Id) || model.Id.Any(char.IsUpper))
            throw new ArgumentException("A model id is lower-case letters, digits, '.', '-' and '_'.", nameof(model));
        if (model.Files.IsDefaultOrEmpty)
            throw new ArgumentException("A model has at least one file.", nameof(model));
        foreach (var f in model.Files)
            if (!IsSafeName(f.FileName))
                throw new ArgumentException("A model file name is a plain name: " + f.FileName, nameof(model));
    }

    private static bool IsSafeName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && name != "." && name != ".." &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private string ModelDir(string id) => Path.Combine(Root, id);

    // ---- licences ----

    public LicenceAcceptance? Acceptance(string modelId)
    {
        lock (_licenceLock)
            return ReadLicences().FirstOrDefault(a => a.ModelId == modelId);
    }

    /// <summary>True when the recorded acceptance is for the licence the catalogue names now (a changed licence
    /// must be read again) and, for a non-commercial one, acknowledged as such.</summary>
    public bool IsLicenceAccepted(VisionModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var a = Acceptance(model.Id);
        return a is not null && a.LicenceId == model.Licence.Id && (model.Licence.AllowsCommercialUse || a.NonCommercialAcknowledged);
    }

    /// <summary>Records that <paramref name="acceptedBy"/> read and accepted the model's licence. A non-commercial
    /// licence is refused unless <paramref name="acknowledgeNonCommercial"/> says the operator knows it bars paid
    /// work.</summary>
    public LicenceDecision AcceptLicence(string modelId, string acceptedBy, bool acknowledgeNonCommercial = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedBy);
        var model = Catalog.Find(modelId);
        if (model is null)
            return new LicenceDecision(false, "no model " + modelId + " in the catalogue");
        if (!model.Licence.AllowsCommercialUse && !acknowledgeNonCommercial)
            return new LicenceDecision(false, model.Name + " is under " + model.Licence.Name + ": it may not be used for commercial work; accept it only with that acknowledged");
        lock (_licenceLock)
        {
            var list = ReadLicences().Where(a => a.ModelId != modelId).ToList();
            list.Add(new LicenceAcceptance(modelId, model.Licence.Id, acceptedBy, DateTime.UtcNow, acknowledgeNonCommercial));
            WriteLicences(list);
        }
        return new LicenceDecision(true, "accepted " + model.Licence.Name + " for " + model.Name);
    }

    public bool RevokeLicence(string modelId)
    {
        lock (_licenceLock)
        {
            var list = ReadLicences();
            var kept = list.Where(a => a.ModelId != modelId).ToList();
            if (kept.Count == list.Count)
                return false;
            WriteLicences(kept);
            return true;
        }
    }

    private List<LicenceAcceptance> ReadLicences()
    {
        var path = Path.Combine(Root, LicencesName);
        if (!File.Exists(path))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var list = new List<LicenceAcceptance>();
            foreach (var e in doc.RootElement.GetProperty("accepted").EnumerateArray())
            {
                list.Add(new LicenceAcceptance(
                    e.GetProperty("model").GetString() ?? "",
                    e.GetProperty("licence").GetString() ?? "",
                    e.GetProperty("by").GetString() ?? "",
                    DateTime.Parse(e.GetProperty("utc").GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    e.GetProperty("nonCommercial").GetBoolean()));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A torn record fails closed: no licence counts as accepted until the operator accepts again.
            return [];
        }
    }

    private void WriteLicences(List<LicenceAcceptance> list)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("accepted");
            foreach (var a in list)
            {
                w.WriteStartObject();
                w.WriteString("model", a.ModelId);
                w.WriteString("licence", a.LicenceId);
                w.WriteString("by", a.AcceptedBy);
                w.WriteString("utc", a.AcceptedUtc.ToString("O", CultureInfo.InvariantCulture));
                w.WriteBoolean("nonCommercial", a.NonCommercialAcknowledged);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        WriteAtomically(Path.Combine(Root, LicencesName), buffer.WrittenSpan);
    }

    private static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes.ToArray());
        File.Move(tmp, path, overwrite: true);
    }

    // ---- installed ----

    /// <summary>What is installed: models with a manifest whose files are all present at their recorded sizes.</summary>
    public IReadOnlyList<InstalledModel> ListInstalled()
    {
        var list = new List<InstalledModel>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var m = ReadManifest(dir);
            if (m is not null)
                list.Add(m);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    public InstalledModel? Installed(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return IsSafeName(modelId) ? ReadManifest(ModelDir(modelId)) : null;
    }

    private static InstalledModel? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            var files = ImmutableArray.CreateBuilder<InstalledFile>();
            foreach (var f in r.GetProperty("files").EnumerateArray())
            {
                var name = f.GetProperty("name").GetString() ?? "";
                var size = f.GetProperty("size").GetInt64();
                var full = Path.Combine(dir, name);
                if (!IsSafeName(name) || !File.Exists(full) || new FileInfo(full).Length != size)
                    return null;
                files.Add(new InstalledFile(f.GetProperty("role").GetString() ?? "", full, f.GetProperty("sha256").GetString() ?? "", size));
            }
            return new InstalledModel(
                r.GetProperty("id").GetString() ?? "", dir, r.GetProperty("licence").GetString() ?? "",
                DateTime.Parse(r.GetProperty("installedUtc").GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                files.ToImmutable());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            // A torn or hand-edited manifest is not an installation; installing again rewrites it.
            return null;
        }
    }

    private static void WriteManifest(VisionModelInfo model, string dir)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", model.Id);
            w.WriteString("licence", model.Licence.Id);
            w.WriteString("installedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartArray("files");
            foreach (var f in model.Files)
            {
                w.WriteStartObject();
                w.WriteString("role", f.Role);
                w.WriteString("name", f.FileName);
                w.WriteString("sha256", f.Sha256);
                w.WriteNumber("size", new FileInfo(Path.Combine(dir, f.FileName)).Length);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        WriteAtomically(Path.Combine(dir, ManifestName), buffer.WrittenSpan);
    }

    /// <summary>Removes an installed model's folder (partial downloads included).</summary>
    public bool Remove(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        if (!IsSafeName(modelId))
            return false;
        var dir = ModelDir(modelId);
        if (!Directory.Exists(dir))
            return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    // ---- install ----

    /// <summary>Downloads and verifies every file of a catalogued model. Refused (nothing fetched) when the show
    /// gate is shut, the entry has no verified checksums, the licence is not accepted, or a gated model has no
    /// token; failed (partial kept) when the network drops or a checksum does not match. Run it again to resume.
    /// Cancellation throws and keeps the partial file.</summary>
    public async Task<ModelInstallResult> InstallAsync(string modelId, IProgress<ModelDownloadProgress>? progress, string? accessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var model = Catalog.Find(modelId);
        if (model is null)
            return new ModelInstallResult(modelId, ModelInstallStatus.Refused, "no model " + modelId + " in the catalogue", 0);
        if (_options.DownloadsAllowed is { } allowed && !allowed())
            return new ModelInstallResult(modelId, ModelInstallStatus.Refused, "downloads wait until the show stops", 0);
        if (!model.IsVerified)
            return new ModelInstallResult(modelId, ModelInstallStatus.Refused, "the catalogue has no verified checksum for " + model.Name + "; it cannot be installed until one is supplied", 0);
        if (!IsLicenceAccepted(model))
            return new ModelInstallResult(modelId, ModelInstallStatus.Refused,
                "the licence for " + model.Name + " (" + model.Licence.Name + ", " + model.Licence.Url + ") has not been accepted", 0);
        if (model.RequiresSeparateAgreement && string.IsNullOrWhiteSpace(accessToken))
            return new ModelInstallResult(modelId, ModelInstallStatus.Refused,
                "the publisher gates " + model.Name + ": accept their agreement on their site and supply an access token", 0);

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = ModelDir(model.Id);
            Directory.CreateDirectory(dir);
            long total = 0;
            var fetched = false;
            foreach (var file in model.Files)
            {
                var outcome = await FetchFileAsync(model, file, dir, progress, accessToken, cancellationToken).ConfigureAwait(false);
                total += outcome.Bytes;
                fetched |= outcome.Fetched;
                if (outcome.Error is not null)
                    return new ModelInstallResult(model.Id, ModelInstallStatus.Failed, file.FileName + ": " + outcome.Error, total);
            }
            var hadManifest = ReadManifest(dir) is not null;
            WriteManifest(model, dir);
            return fetched || !hadManifest
                ? new ModelInstallResult(model.Id, ModelInstallStatus.Installed, "installed " + model.Name, total)
                : new ModelInstallResult(model.Id, ModelInstallStatus.AlreadyInstalled, model.Name + " is already installed", 0);
        }
        finally
        {
            _installGate.Release();
        }
    }

    private readonly record struct FileOutcome(long Bytes, bool Fetched, string? Error);

    private async Task<FileOutcome> FetchFileAsync(VisionModelInfo model, VisionModelFile file, string dir, IProgress<ModelDownloadProgress>? progress, string? accessToken, CancellationToken ct)
    {
        var final = Path.Combine(dir, file.FileName);
        var partial = final + ".partial";
        var expected = file.Sha256!;
        if (File.Exists(final))
        {
            if (string.Equals(await HashFileAsync(final, ct).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
                return new FileOutcome(0, false, null);
            File.Delete(final);
        }
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri))
            return new FileOutcome(0, false, "the address is not a URL");
        var cap = file.SizeBytes > 0 ? file.SizeBytes : _options.MaxBytesPerFile;
        if (uri.IsFile)
            return await CopyLocalAsync(model, file, uri.LocalPath, partial, final, cap, progress, ct).ConfigureAwait(false);
        var loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && _options.AllowLoopbackHttp;
        if (uri.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
            return new FileOutcome(0, false, "only HTTPS downloads are allowed");

        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > cap)
        {
            File.Delete(partial);
            have = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        long received = 0;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            {
                // The partial may already be whole; the checksum decides.
                return await FinishAsync(partial, final, expected, 0, ct).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode)
                return new FileOutcome(0, false, string.Create(CultureInfo.InvariantCulture, $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}"));
            var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange?.From != have)
            {
                File.Delete(partial);
                return new FileOutcome(0, false, "the server resumed at the wrong place; the partial download was discarded, install again");
            }
            if (!append)
                have = 0;
            var length = response.Content.Headers.ContentLength;
            var total = length is { } l ? have + l : file.SizeBytes;
            if (total > cap)
                return new FileOutcome(0, false, string.Create(CultureInfo.InvariantCulture, $"the file is {total} bytes, more than the {cap} expected"));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (append)
                await HashIntoAsync(partial, hash, ct).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var fs = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using (fs.ConfigureAwait(false))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
                    try
                    {
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var stallAfter = _options.StallTimeout ?? TimeSpan.FromSeconds(60);
                        while (true)
                        {
                            stall.CancelAfter(stallAfter);
                            int n;
                            try
                            {
                                n = await stream.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                return new FileOutcome(received, true, string.Create(CultureInfo.InvariantCulture, $"the download stalled at {have + received} bytes; install again to resume"));
                            }
                            if (n == 0)
                                break;
                            if (have + received + n > cap)
                            {
                                await fs.DisposeAsync().ConfigureAwait(false);
                                File.Delete(partial);
                                return new FileOutcome(received, true, string.Create(CultureInfo.InvariantCulture, $"the file grew past the {cap} bytes expected"));
                            }
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            hash.AppendData(buffer, 0, n);
                            received += n;
                            progress?.Report(new ModelDownloadProgress(model.Id, file.FileName, have + received, total));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            if (length is { } expectLength && received != expectLength)
                return new FileOutcome(received, true, string.Create(CultureInfo.InvariantCulture, $"the connection closed at {have + received} of {have + expectLength} bytes; install again to resume"));
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            return Finish(partial, final, expected, actual, received);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            // The partial file is kept: the next install resumes from its end.
            return new FileOutcome(received, received > 0, string.Create(CultureInfo.InvariantCulture, $"interrupted at {have + received} bytes ({ex.Message}); install again to resume"));
        }
    }

    private async Task<FileOutcome> FinishAsync(string partial, string final, string expected, long received, CancellationToken ct)
    {
        if (!File.Exists(partial))
            return new FileOutcome(received, false, "the server refused the range and nothing was kept");
        var actual = await HashFileAsync(partial, ct).ConfigureAwait(false);
        return Finish(partial, final, expected, actual, received);
    }

    private static FileOutcome Finish(string partial, string final, string expected, string actual, long received)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            return new FileOutcome(received, true, "checksum mismatch (expected " + expected + ", got " + actual + "); the download was discarded");
        }
        File.Move(partial, final, overwrite: true);
        return new FileOutcome(received, true, null);
    }

    private static async Task<FileOutcome> CopyLocalAsync(VisionModelInfo model, VisionModelFile file, string source, string partial, string final, long cap, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(source))
            return new FileOutcome(0, false, "the local file is not there");
        var length = new FileInfo(source).Length;
        if (length > cap)
            return new FileOutcome(0, false, string.Create(CultureInfo.InvariantCulture, $"the file is {length} bytes, more than the {cap} expected"));
        var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using (src.ConfigureAwait(false))
        {
            var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using (dst.ConfigureAwait(false))
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        progress?.Report(new ModelDownloadProgress(model.Id, file.FileName, length, length));
        var actual = await HashFileAsync(partial, ct).ConfigureAwait(false);
        return Finish(partial, final, file.Sha256!, actual, length);
    }

    private static async Task HashIntoAsync(string path, IncrementalHash hash, CancellationToken ct)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                int n;
                while ((n = await fs.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                    hash.AppendData(buffer, 0, n);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await HashIntoAsync(path, hash, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Re-reads an installed model's files and checks them against the manifest.</summary>
    public async Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken)
    {
        var m = Installed(modelId);
        if (m is null)
            return false;
        foreach (var f in m.Files)
            if (!string.Equals(await HashFileAsync(f.Path, cancellationToken).ConfigureAwait(false), f.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>The one line the desk shows for a catalogue entry: name, licence, and what stands in its way.</summary>
    public static string Describe(VisionModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var sb = new StringBuilder();
        sb.Append(model.Name).Append(" — ").Append(model.Licence.Name);
        if (!model.Licence.AllowsCommercialUse)
            sb.Append(" (non-commercial)");
        if (!model.IsVerified)
            sb.Append(" (unverified catalogue entry)");
        return sb.ToString();
    }

    public void Dispose()
    {
        _http.Dispose();
        _installGate.Dispose();
    }
}
