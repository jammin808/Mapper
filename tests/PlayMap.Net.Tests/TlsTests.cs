using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Tests;

/// <summary>The node's certificate (Show Bus §10): made once and kept with its key protected, the contract's shape
/// (ECDSA P-256, the node's name, localhost among its names, 825 days), renewed near its end; and the client's pins:
/// first use, pinned, CERTIFICATE CHANGED, a hello that names another certificate.</summary>
public class TlsTests
{
    private static readonly string A = "sha256:" + new string('a', 64);
    private static readonly string B = "sha256:" + new string('b', 64);

    [Fact]
    public void TheCertificateIsKeptBetweenStartsWithItsKeyProtected()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-id-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            string first;
            using (var made = TlsIdentity.LoadOrCreate(folder.FullName, "Stage left", now, ["rack-1"], [IPAddress.Parse("10.0.0.9")]))
            {
                first = made.Fingerprint;
                Assert.True(made.Persisted);
                Assert.True(made.Certificate.HasPrivateKey);
                Assert.Equal("CN=Stage left", made.Certificate.Subject);
                using var key = made.Certificate.GetECDsaPublicKey();
                Assert.Equal(256, key!.KeySize);
                var life = made.Certificate.NotAfter - made.Certificate.NotBefore;
                Assert.InRange(life.TotalDays, 825, 826.01);
                var san = made.Certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
                Assert.Contains("localhost", san.EnumerateDnsNames(), StringComparer.Ordinal);
                Assert.Contains("rack-1", san.EnumerateDnsNames(), StringComparer.Ordinal);
                Assert.Contains(IPAddress.Parse("10.0.0.9"), san.EnumerateIPAddresses());
                Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(made.PublicDer)), made.Fingerprint);
            }
            var keyFile = Path.Combine(folder.FullName, TlsIdentity.KeyFileName);
            Assert.True(File.Exists(keyFile));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
            using (var again = TlsIdentity.LoadOrCreate(folder.FullName, "Renamed", now.AddDays(10)))
                Assert.Equal(first, again.Fingerprint);
            using var renewed = TlsIdentity.LoadOrCreate(folder.FullName, "Stage left", now.AddDays(800));
            Assert.NotEqual(first, renewed.Fingerprint);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ADamagedKeyFileIsReplacedNotTrusted()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-id-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, TlsIdentity.KeyFileName), [1, 2, 3, 4]);
            using var made = TlsIdentity.LoadOrCreate(folder.FullName, "Stage left", DateTimeOffset.UtcNow);
            Assert.True(made.Certificate.HasPrivateKey);
            Assert.True(TlsFingerprint.IsValid(made.Fingerprint));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void PinsSayFirstUsePinnedChangedAndMismatch()
    {
        var pins = new PinStore();
        var node = PinStore.NodeKey("9f8e7d6c");
        Assert.Equal("node:9f8e7d6c", node);
        Assert.Equal("addr:foh-pc:9924", PinStore.AddressKey("FOH-PC", 9924));

        Assert.Equal(PinVerdict.FirstUse, pins.Check(node, A, null));
        Assert.Equal(PinVerdict.FirstUse, pins.Check(node, A, A.ToUpperInvariant().Replace("SHA256:", "sha256:", StringComparison.Ordinal)));
        Assert.Equal(PinVerdict.Mismatch, pins.Check(node, A, B));
        Assert.Equal(PinVerdict.Mismatch, pins.Check(node, "not a fingerprint", null));
        pins.Pin(node, A);
        Assert.Equal(PinVerdict.Pinned, pins.Check(node, A, B));
        Assert.Equal(PinVerdict.Changed, pins.Check(node, B, B));
        Assert.StartsWith("CERTIFICATE CHANGED", PinStore.Words(PinVerdict.Changed, "FOH", B), StringComparison.Ordinal);
        Assert.False(PinStore.Accepts(PinVerdict.Changed));
        Assert.True(PinStore.Accepts(PinVerdict.FirstUse));
        pins.Pin(node, B);   // the operator accepted the new one
        Assert.Equal(PinVerdict.Pinned, pins.Check(node, B, null));
    }

    [Fact]
    public void PinsAreKeptInTheirFileAcrossStarts()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("playmap-pins-").FullName, "pins.json");
        try
        {
            new PinStore(path).Pin(PinStore.NodeKey("ab12cd34"), A);
            var again = new PinStore(path);
            Assert.Equal(A, again.Get(PinStore.NodeKey("ab12cd34")));
            Assert.True(again.Forget(PinStore.NodeKey("ab12cd34")));
            Assert.Null(new PinStore(path).Get(PinStore.NodeKey("ab12cd34")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
