using System.Text;
using System.Text.Json;
using PlayMap.Devices.Model;
using PlayMap.Net.Tls;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Devices;

/// <summary>
/// The node's secrets — projector passwords and the assistant's key — by reference, kept in the node's keys folder
/// (<see cref="NodeHome.KeysFolder"/>) and protected at rest the way the TLS key is: Windows DPAPI for this user
/// where it runs, an owner-only file (mode 600) elsewhere; written beside and moved over, so a crash never leaves
/// half a file. Nothing reads a secret back out but a driver's handshake and the assistant's provider: no line,
/// no journal row, no STATE or context ever carries one, and <see cref="ToString"/> says only what it is.
/// Thread-safe.
/// </summary>
public sealed class NodeCredentialStore : ICredentialStore
{
    public const string FileName = "playmap-credentials.bin";

    /// <summary>The reference under which the assistant's API key is kept.</summary>
    public const string AssistantKey = "assistant";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlayMap node credentials");

    private readonly string? _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Credential> _items = new(StringComparer.Ordinal);
    private long _version;

    /// <param name="folder">The keys folder; null keeps the secrets in memory only (tests, a node without a home).</param>
    public NodeCredentialStore(string? folder)
    {
        _path = folder is null ? null : Path.Combine(folder, FileName);
        Load();
    }

    /// <summary>Why the store could not be read or written ("" while it can).</summary>
    public string Problem { get; private set; } = "";

    /// <summary>Moves on every change (a provider made with an older key is made again).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The references that hold a secret, in order.</summary>
    public IReadOnlyList<string> References
    {
        get
        {
            lock (_gate)
                return _items.Keys.Order(StringComparer.Ordinal).ToList();
        }
    }

    public Credential? Find(string reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;
        lock (_gate)
            return _items.GetValueOrDefault(reference);
    }

    /// <summary>A reference is 1–64 characters of [A-Za-z0-9_.-]: it rides in lines and project files.</summary>
    public static bool IsReference(string? reference) =>
        reference is { Length: > 0 and <= 64 } r && r.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Keeps a secret under a reference (replacing what was there) and writes the store.</summary>
    public void Set(string reference, Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsReference(reference))
            throw new ArgumentException("A credential reference is 1–64 letters, digits, '_', '-' or '.'.", nameof(reference));
        lock (_gate)
        {
            _items[reference] = credential;
            Save();
        }
        Interlocked.Increment(ref _version);
    }

    /// <summary>Forgets a secret; false when there was none.</summary>
    public bool Remove(string reference)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(reference);
            if (removed)
                Save();
        }
        if (removed)
            Interlocked.Increment(ref _version);
        return removed;
    }

    /// <summary>Every secret this store holds (for masking: a redactor hides them wherever they would appear).</summary>
    public IReadOnlyList<string> Secrets()
    {
        lock (_gate)
            return _items.Values.Select(c => c.Secret).Where(s => s.Length > 0).ToList();
    }

    public override string ToString() => "credential store";

    private sealed record Entry(string User, string Secret);

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
            return;
        try
        {
            var bytes = DurableFile.ReadAllBytes(_path);
            if (OperatingSystem.IsWindows())
                bytes = Dpapi.Unprotect(bytes, Entropy);
            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(bytes) ?? [];
            foreach (var (reference, entry) in entries)
            {
                if (IsReference(reference) && entry is not null)
                    _items[reference] = new Credential(entry.User ?? "", entry.Secret ?? "");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            // Unreadable (another user's DPAPI, a damaged file): the store starts empty and says why; the file is
            // left where it is so nothing is lost silently.
            Problem = "the credential store could not be read (" + ex.GetType().Name + ") — set the secrets again";
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            var entries = _items.ToDictionary(kv => kv.Key, kv => new Entry(kv.Value.User, kv.Value.Secret), StringComparer.Ordinal);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (OperatingSystem.IsWindows())
                bytes = Dpapi.Protect(bytes, Entropy);
            var folder = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(folder);
            var temp = _path + ".new";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var f = new FileStream(temp, options))
            {
                f.Write(bytes);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DurableFile.MoveIntoPlace(temp, _path);
            Problem = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Kept in memory for this run; the words say it will not survive a restart.
            Problem = "the credential store could not be written (" + ex.GetType().Name + "): secrets last until the node stops";
        }
    }
}
