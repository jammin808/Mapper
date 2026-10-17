using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Storage;

namespace PlayMap.Net.Tls;

/// <summary>A certificate as the show network names it (Show Bus §10): "sha256:" and the lowercase hex of SHA-256
/// over its DER bytes. The hello carries this node's; a client pins the one it met.</summary>
public static class TlsFingerprint
{
    public const string Prefix = "sha256:";

    public static string Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Of(certificate.RawData);
    }

    public static string Of(ReadOnlySpan<byte> der) => Prefix + Convert.ToHexStringLower(SHA256.HashData(der));

    /// <summary>"sha256:" and 64 lowercase hex characters.</summary>
    public static bool IsValid(string? fingerprint) =>
        fingerprint is { Length: 71 } f && f.StartsWith(Prefix, StringComparison.Ordinal) && f.AsSpan(Prefix.Length).ContainsOnlyLowerHex();

    /// <summary>A fingerprint as read from a peer or typed by a person: trimmed and lower-cased, "" when it is not one.</summary>
    public static string Normal(string? fingerprint)
    {
        var f = (fingerprint ?? "").Trim().ToLowerInvariant();
        return IsValid(f) ? f : "";
    }

    /// <summary>The first sixteen hex digits in fours — what a person compares across two screens.</summary>
    public static string Short(string fingerprint) =>
        IsValid(fingerprint) ? string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(Prefix.Length + i * 4, 4))) : "";

    private static bool ContainsOnlyLowerHex(this ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        return true;
    }
}

/// <summary>This install's own certificate (Show Bus §10): self-signed ECDSA P-256, 825 days (Apple devices refuse
/// longer), renewed at a start within 30 days of its end; the subject is the node's name; the names it answers to are
/// this machine's name, localhost and the addresses it has now. Made once and kept in the node's folder with its
/// private key protected at rest — Windows DPAPI (this user) where it runs, file mode 600 elsewhere — and never
/// copied to another machine. A folder that cannot be written leaves a certificate for this run only (the
/// fingerprint then changes at the next start, and pinned peers say CERTIFICATE CHANGED).</summary>
public sealed class TlsIdentity : IDisposable
{
    public const string KeyFileName = "playmap-tls.key";
    public const string CertFileName = "playmap-tls.cer";

    public static readonly TimeSpan Life = TimeSpan.FromDays(825);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlayMap node TLS key");

    private TlsIdentity(X509Certificate2 certificate, bool persisted)
    {
        Certificate = certificate;
        Fingerprint = TlsFingerprint.Of(certificate);
        PublicDer = certificate.Export(X509ContentType.Cert);
        Persisted = persisted;
    }

    /// <summary>The certificate with its private key (what the HTTPS listener serves).</summary>
    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    /// <summary>The certificate alone, DER — <c>GET /tls.cer</c>.</summary>
    public byte[] PublicDer { get; }

    /// <summary>False when the folder could not keep it (this run's certificate only).</summary>
    public bool Persisted { get; }

    public DateTime NotAfterUtc => Certificate.NotAfter.ToUniversalTime();

    /// <summary>The kept certificate, or a new one (first run, unreadable, ending within 30 days).</summary>
    public static TlsIdentity LoadOrCreate(string folder, string nodeName, DateTimeOffset now,
        IEnumerable<string>? dnsNames = null, IEnumerable<IPAddress>? addresses = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        log ??= NullLogger.Instance;
        var keyPath = Path.Combine(folder, KeyFileName);
        if (TryLoad(keyPath, log) is { } kept)
        {
            if (!NeedsRenewal(kept, now))
                return new TlsIdentity(kept, persisted: true);
            log.LogInformation("The node's certificate ends {End:u}: a new one is made.", kept.NotAfter.ToUniversalTime());
            kept.Dispose();
        }
        using var made = Create(nodeName, dnsNames ?? DefaultNames(), addresses ?? DefaultAddresses(), now);
        var pkcs12 = made.Export(X509ContentType.Pkcs12);
        var persisted = TrySave(folder, keyPath, pkcs12, made.Export(X509ContentType.Cert), log);
        return new TlsIdentity(Import(pkcs12), persisted);
    }

    /// <summary>A new self-signed certificate (its key is the one returned with it). localhost and the loopback
    /// addresses are always among its names, beside the ones given.</summary>
    public static X509Certificate2 Create(string name, IEnumerable<string> dnsNames, IEnumerable<IPAddress> addresses, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dnsNames);
        ArgumentNullException.ThrowIfNull(addresses);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + CommonName(name)), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames.Append("localhost").Select(d => d.Trim()).Where(d => d.Length is > 0 and < 254 && Uri.CheckHostName(d) == UriHostNameType.Dns).Distinct(StringComparer.OrdinalIgnoreCase))
            san.AddDnsName(dns);
        foreach (var ip in addresses.Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).Distinct())
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddDays(-1), now + Life);
    }

    public static bool NeedsRenewal(X509Certificate2 certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero) - now < RenewBefore;
    }

    private static IEnumerable<string> DefaultNames() => [Environment.MachineName];

    private static IEnumerable<IPAddress> DefaultAddresses() => NetAddress.LanAddresses();

    private static X509Certificate2? TryLoad(string keyPath, ILogger log)
    {
        try
        {
            if (!File.Exists(keyPath))
                return null;
            var bytes = DurableFile.ReadAllBytes(keyPath);
            if (OperatingSystem.IsWindows())
                bytes = Dpapi.Unprotect(bytes, Entropy);
            var cert = Import(bytes);
            if (cert.HasPrivateKey)
                return cert;
            cert.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Unreadable (another user's DPAPI, a damaged file): a new certificate replaces it; pinned peers say so.
            log.LogWarning(ex, "The node's certificate could not be read: a new one is made.");
            return null;
        }
    }

    private static bool TrySave(string folder, string keyPath, byte[] pkcs12, byte[] der, ILogger log)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var keep = OperatingSystem.IsWindows() ? Dpapi.Protect(pkcs12, Entropy) : pkcs12;
            WriteAtomically(keyPath, keep, privateFile: true);
            WriteAtomically(Path.Combine(folder, CertFileName), der, privateFile: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            log.LogWarning(ex, "The node's certificate could not be kept: this run serves one of its own.");
            return false;
        }
    }

    /// <summary>Written beside, then moved over: a crash mid-write never leaves half a key. A private file is created
    /// owner-only (mode 600) where the platform has modes, before a byte of it is written.</summary>
    private static void WriteAtomically(string path, byte[] bytes, bool privateFile)
    {
        var temp = path + ".new";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (privateFile && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(temp, options))
        {
            f.Write(bytes);
        }
        if (privateFile && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        DurableFile.MoveIntoPlace(temp, path);
    }

    /// <summary>A key TLS can use on this platform: a key container on Windows (SChannel cannot use an ephemeral
    /// key), in memory elsewhere.</summary>
    private static X509Certificate2 Import(byte[] pkcs12) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

    private static string CommonName(string? name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c) && c is not (',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')).ToArray()).Trim();
        return n.Length == 0 ? "PlayMap" : n.Length > 64 ? n[..64] : n;
    }

    public void Dispose() => Certificate.Dispose();
}

/// <summary>Windows DPAPI for this user: the key file is readable only by the account that made it, on this
/// machine. System32's crypt32 by full search path, never a DLL planted beside a portable exe. Public so every
/// secret the node keeps at rest (the TLS key, the credential store) is protected the one way.</summary>
[SupportedOSPlatform("windows")]
public static class Dpapi
{
    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

    private static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var input = new DataBlob { Size = data.Length, Data = dataHandle.AddrOfPinnedObject() };
            var salt = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok)
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
                _ = LocalFree(output.Data);
            dataHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
