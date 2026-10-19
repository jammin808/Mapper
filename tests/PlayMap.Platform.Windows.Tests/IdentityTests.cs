using PlayMap.Core;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The node's identity, pure: every edition family from a fake <c>GetProductInfo</c> and <c>EditionID</c> with its support
/// tier, the vendor tier and support level per vendor, and a locked driver set against a changed GPU driver — the amber row naming
/// both versions with the fix line MACHINE LOCK.</summary>
public sealed class IdentityTests
{
    public static readonly TheoryData<string, uint, EditionFamily, bool, bool, SupportTier> Editions = new()
    {
        { "Core", 0x65, EditionFamily.Home, false, false, SupportTier.Home },
        { "CoreSingleLanguage", 0x64, EditionFamily.Home, false, false, SupportTier.Home },
        { "Professional", 0x30, EditionFamily.Pro, false, false, SupportTier.Managed },
        { "ProfessionalWorkstation", 0xA1, EditionFamily.Workstation, false, false, SupportTier.Managed },
        { "Enterprise", 0x04, EditionFamily.Enterprise, false, false, SupportTier.Managed },
        { "EnterpriseS", 0x7D, EditionFamily.Enterprise, true, false, SupportTier.Managed },
        { "Education", 0x79, EditionFamily.Education, false, false, SupportTier.Managed },
        { "IoTEnterpriseS", 0xBF, EditionFamily.Iot, true, true, SupportTier.Ltsc },
        { "ServerDatacenter", 0x08, EditionFamily.Server, false, false, SupportTier.Unsupported },
        { "SomethingNew", 0x1234, EditionFamily.Unknown, false, false, SupportTier.Unknown },
    };

    [Theory]
    [MemberData(nameof(Editions))]
    public void EveryEditionFamilyIsReadWithItsTier(string editionId, uint product, EditionFamily family, bool ltsc, bool iot, SupportTier tier)
    {
        var os = EditionTable.Classify(editionId, product, "25H2", 26200, 6899);
        Assert.Equal((editionId, family, ltsc, iot), (os.Edition, os.Family, os.Ltsc, os.Iot));
        Assert.Equal(tier, EditionTable.Tier(os));
        // The product type alone (no EditionID) reads the same family, where the table knows it.
        var byType = EditionTable.Classify("", product, "25H2", 26200, 6899);
        Assert.Equal(family, byType.Family);
        Assert.Equal(family == EditionFamily.Unknown ? "product 0x1234" : byType.Edition, byType.Edition);
    }

    [Fact]
    public void ABuildBelow24H2IsUnsupportedOnEveryEditionAndTheVersionComesFromTheBuildWhenUnsaid()
    {
        var old = EditionTable.Classify("Professional", 0x30, "", 22631, 4460);
        Assert.Equal("23H2", old.Version);
        Assert.Equal(SupportTier.Unsupported, EditionTable.Tier(old));
        Assert.Equal("", EditionTable.Classify("Professional", 0, "", 27000, 1).Version);
        Assert.Equal(["ltsc", "managed", "home", "unsupported", "unknown"],
            new[] { SupportTier.Ltsc, SupportTier.Managed, SupportTier.Home, SupportTier.Unsupported, SupportTier.Unknown }.Select(EditionTable.Word));
        Assert.Equal("Windows 11 IoT Enterprise LTSC", EditionTable.Name(EditionTable.Classify("IoTEnterpriseS", 0, "24H2", 26100, 1)));
    }

    [Fact]
    public void EachVendorHasItsTierAndSupportLevel()
    {
        Assert.Equal((VendorTier.NvidiaRtxPro, SupportLevel.Tier1), Place(VendorPolicy.Nvidia, "NVIDIA RTX PRO 4000 Blackwell", true));
        Assert.Equal((VendorTier.NvidiaRtx, SupportLevel.Tier1), Place(VendorPolicy.Nvidia, "NVIDIA GeForce RTX 5090", false));
        // The name alone never makes an RTX PRO: NVAPI's workstation probe does.
        Assert.Equal((VendorTier.NvidiaRtx, SupportLevel.Tier1), Place(VendorPolicy.Nvidia, "NVIDIA RTX PRO 4000 Blackwell", null));
        Assert.Equal((VendorTier.Other, SupportLevel.Unsupported), Place(VendorPolicy.Nvidia, "NVIDIA GeForce GTX 1080", false));
        Assert.Equal((VendorTier.Radeon, SupportLevel.ValidatedPerDriver), Place(VendorPolicy.Amd, "AMD Radeon RX 7900 XTX", null));
        Assert.Equal((VendorTier.Radeon, SupportLevel.ValidatedPerDriver), Place(VendorPolicy.Amd, "AMD Radeon RX 9070 XT", null));
        Assert.Equal((VendorTier.Radeon, SupportLevel.ValidatedPerDriver), Place(VendorPolicy.Amd, "AMD Radeon PRO W7800", null));
        Assert.Equal((VendorTier.Radeon, SupportLevel.Unsupported), Place(VendorPolicy.Amd, "AMD Radeon RX 6800", null));
        Assert.Equal((VendorTier.IntelArc, SupportLevel.BestEffort), Place(VendorPolicy.Intel, "Intel(R) Arc(TM) B580 Graphics", null));
        Assert.Equal((VendorTier.Other, SupportLevel.Unsupported), Place(VendorPolicy.Intel, "Intel(R) UHD Graphics 770", null));
        Assert.Equal((VendorTier.Software, SupportLevel.Unsupported), Place(VendorPolicy.Microsoft, "Microsoft Basic Render Driver", null));

        static (VendorTier, SupportLevel) Place(int vendor, string name, bool? workstation)
        {
            var tier = VendorPolicy.Tier(vendor, name, workstation);
            return (tier, VendorPolicy.Support(tier, name));
        }
    }

    private static NodeIdentity Node(string driver, string ffmpeg = "n9.0.2") => new(
        BuildInfo.Parse("0.2.0+17.abc1234"),
        EditionTable.Classify("IoTEnterpriseS", 0xBF, "24H2", 26100, 4061),
        new CpuIdentity("AMD Ryzen 9 9950X3D", 32, [32], 512, true, false),
        [new AdapterIdentity(0x123a4, VendorPolicy.Nvidia, 0x2bb1, "NVIDIA RTX PRO 4000 Blackwell", driver, "r580_00", VendorTier.NvidiaRtxPro,
            SupportLevel.Tier1, true, 24_576)],
        new RuntimeIdentity("1.619.2", ffmpeg, "3.06"));

    [Fact]
    public void ALockedSetAgainstAChangedGpuDriverIsAnAmberRowNamingBothVersionsFixedByMachineLock()
    {
        var locked = LockedDriverSet.Of(Node("582.16"), 1_760_600_000_000);
        Assert.Equal("26100.4061", locked.Os);
        Assert.Equal("0.2.0+17.abc1234", locked.PlayMap);
        var same = LockedDriverSet.Of(Node("582.16"), 1_760_700_000_000);
        Assert.True(locked.SameDrivers(same));

        var now = LockedDriverSet.Of(Node("591.44"), 1_760_700_000_000);
        var drift = Assert.Single(locked.DriftTo(now));
        Assert.Equal(("gpu:10de:2bb1", "582.16", "591.44"), (drift.Item, drift.Locked, drift.Now));
        Assert.Equal("NVIDIA RTX PRO 4000 Blackwell driver 582.16 → 591.44 since the driver set was locked", drift.Words);

        var rows = MachineRows.Engine(Node("591.44").Os, locked, now, []);
        var row = Assert.Single(rows);
        Assert.Equal(("lock.drift:gpu:10de:2bb1", Light.Amber, "591.44", "MACHINE LOCK"), (row.Item, row.Light, row.Value, row.Fix));
        Assert.Contains("582.16 → 591.44", row.Words, StringComparison.Ordinal);

        // Two moves at once are two rows, in the set's order; an adapter gone and one added are said as such.
        var moved = LockedDriverSet.Of(Node("591.44", "n9.0.3"), 1) with { Adapters = [new LockedAdapter(VendorPolicy.Amd, 0x744c, "AMD Radeon RX 7900 XTX", "32.0.21013.1000")] };
        var drifts = locked.DriftTo(moved);
        Assert.Equal(["ffmpeg", "gpu:10de:2bb1", "gpu:1002:744c"], drifts.Select(d => d.Item));
        Assert.Equal("NVIDIA RTX PRO 4000 Blackwell 582.16 gone since the driver set was locked", drifts[1].Words);
        Assert.Equal("AMD Radeon RX 7900 XTX 32.0.21013.1000 added since the driver set was locked", drifts[2].Words);
    }
}
