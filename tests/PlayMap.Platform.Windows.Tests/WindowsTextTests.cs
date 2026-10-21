using System.Buffers.Binary;
using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The text and bytes the platform reads, parsed purely (both jobs): Windows Firewall's rule strings — a profile list, a rule for
/// every program, a program path with environment variables, an inactive, blocking or outbound rule never counted — and a PE32 or PE32+
/// image with and without a certificate table; and the screen saver from its two readings.</summary>
public sealed class WindowsTextTests
{
    private const string Exe = @"C:\Program Files\PlayMap\PlayMap.exe";

    private static string Expand(string path) => path.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ARuleCoversPublicWhenActiveAllowInboundForThisProgramOnPublic()
    {
        Assert.True(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Public|App={Exe}|Name=PlayMap|"], Exe));
        // A profile list names Public among others; a rule with no Profile key covers every profile.
        Assert.True(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Private|Profile=Public|App={Exe}|"], Exe));
        Assert.True(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=In|App={Exe}|"], Exe));
        Assert.False(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Private|Profile=Domain|App={Exe}|"], Exe));
        // A rule with no App key lets every program in.
        Assert.True(FirewallRules.CoverPublic(["v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Public|LPort=9920|Protocol=6|"], Exe));
        // Paths compare without case.
        Assert.True(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Public|App={Exe.ToUpperInvariant()}|"], Exe));
    }

    [Fact]
    public void AProgramPathWithEnvironmentVariablesIsExpandedBeforeItIsCompared()
    {
        string[] rule = [@"v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Public|App=%ProgramFiles%\PlayMap\PlayMap.exe|"];
        Assert.True(FirewallRules.CoverPublic(rule, Exe, Expand));
        // Unexpanded, the stored path is not this program's.
        Assert.False(FirewallRules.CoverPublic(rule, Exe));
    }

    [Fact]
    public void AnInactiveBlockingOrOutboundRuleIsNeverCounted()
    {
        Assert.False(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=FALSE|Dir=In|Profile=Public|App={Exe}|"], Exe));
        Assert.False(FirewallRules.CoverPublic([$"v2.30|Action=Block|Active=TRUE|Dir=In|Profile=Public|App={Exe}|"], Exe));
        Assert.False(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=Out|Profile=Public|App={Exe}|"], Exe));
        Assert.False(FirewallRules.CoverPublic([$"v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Public|App=C:\\Other\\Other.exe|"], Exe));
        Assert.False(FirewallRules.CoverPublic(["", "garbage", "v2.30|"], Exe));
        Assert.False(FirewallRules.CoverPublic([], Exe));
    }

    /// <summary>A PE image's head: the DOS stub pointing at the PE header, the optional header's magic, and the security directory's
    /// size (the fifth data directory) when <paramref name="certificateBytes"/> is set.</summary>
    private static byte[] Image(ushort magic, uint certificateBytes)
    {
        var image = new byte[1024];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        const int Pe = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), Pe);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(Pe), 0x00004550);
        var optional = Pe + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), magic);
        var directories = optional + (magic == 0x20b ? 112 : 96);
        var security = directories + (4 * 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(security), certificateBytes == 0 ? 0u : 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(security + 4), certificateBytes);
        return image;
    }

    [Fact]
    public void APe32OrPe32PlusImageCarriesACertificateTableOnlyWhenItsSecurityDirectoryIsSet()
    {
        Assert.True(PeImage.HasCertificateTable(Image(0x10b, 0x1F00)));
        Assert.True(PeImage.HasCertificateTable(Image(0x20b, 0x1F00)));
        Assert.False(PeImage.HasCertificateTable(Image(0x10b, 0)));
        Assert.False(PeImage.HasCertificateTable(Image(0x20b, 0)));
        // A PE32+ image read as PE32 would find the security entry 16 bytes early: the magic decides where it is.
        var plus = Image(0x20b, 0x1F00);
        BinaryPrimitives.WriteUInt16LittleEndian(plus.AsSpan(0x80 + 24), 0x10b);
        Assert.False(PeImage.HasCertificateTable(plus));
        // Not an image: no MZ, no PE signature, an unknown magic, or too short.
        var notMz = Image(0x20b, 0x1F00);
        notMz[0] = 0;
        Assert.False(PeImage.HasCertificateTable(notMz));
        var notPe = Image(0x20b, 0x1F00);
        notPe[0x80] = 0;
        Assert.False(PeImage.HasCertificateTable(notPe));
        Assert.False(PeImage.HasCertificateTable(Image(0x107, 0x1F00)));
        Assert.False(PeImage.HasCertificateTable(Image(0x20b, 0x1F00).AsSpan(0, 0x100)));
        Assert.False(PeImage.HasCertificateTable([]));
    }

    [Fact]
    public void TheScreenSaverIsOnOnlyWhenItsFlagIsSetAndOneIsChosen()
    {
        Assert.Equal(1, ScreenSaverReading.State(active: true, chosen: true));
        // The flag reads TRUE with "(None)" chosen: off.
        Assert.Equal(0, ScreenSaverReading.State(active: true, chosen: false));
        Assert.Equal(0, ScreenSaverReading.State(active: false, chosen: true));
        Assert.Equal(0, ScreenSaverReading.State(active: null, chosen: false));
        Assert.Equal(0, ScreenSaverReading.State(active: false, chosen: null));
        // Readings that do not decide are unknown, never a guess.
        Assert.Equal(-1, ScreenSaverReading.State(active: true, chosen: null));
        Assert.Equal(-1, ScreenSaverReading.State(active: null, chosen: true));
        Assert.Equal(-1, ScreenSaverReading.State(active: null, chosen: null));
    }
}
