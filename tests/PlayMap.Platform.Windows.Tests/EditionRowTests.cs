using PlayMap.Core.Bus;
using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The edition's rows (D4), pure: on Home a grey information row for each layer it lacks with its Settings path and no Update
/// policy written; Smart App Control evaluating on an unsigned build is amber with its words, off has no row; a projector output on
/// the primary display is amber; a network on Public with no rule is amber; Home 24H2 after 13 Oct 2026 is past its servicing, 25H2 is
/// not, a version the table lacks is grey; IoT Enterprise LTSC has none of Home's rows; Pro and Pro for Workstations have the kiosk's
/// information row (no Shell Launcher) and nothing else of Home's, Enterprise, Education and IoT none.</summary>
public sealed class EditionRowTests
{
    private static readonly DateOnly AfterHome24H2 = new(2026, 10, 19);

    private static MachineReadings On(string edition, uint product, string version, int build) =>
        new() { Os = EditionTable.Classify(edition, product, version, build, 7019) };

    private static MachineFact? Row(IEnumerable<MachineFact> rows, string item) => rows.FirstOrDefault(r => r.Item == item);

    [Fact]
    public void HomeHasAnInformationRowForEachLayerItLacksAndNoUpdatePolicy()
    {
        var rows = MachineRows.Build(On("Core", 0x65, "26H2", 26300), AfterHome24H2);
        var info = rows.Where(r => r.Value == MachineRows.Information).ToList();
        Assert.Equal(["edition.updates", "edition.kiosk", "edition.displays", "edition.remote"], info.Select(r => r.Item));
        Assert.All(info, r => Assert.Equal(Light.Grey, r.Light));
        Assert.All(info, r => Assert.Null(r.Fix));
        Assert.Equal("updates cannot be deferred on this edition: pause them past the show in Settings ▸ Windows Update", info[0].Words);
        Assert.Contains("keep the desk's monitor primary", info[2].Words, StringComparison.Ordinal);
        // Home has no Update client policies: no Update policy row, and nothing offers to write one.
        Assert.DoesNotContain(rows, r => r.Item == "updates");
        Assert.DoesNotContain(rows, r => r.Fix is not null && r.Fix.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        var edition = Row(rows, "os.edition")!;
        Assert.Equal((Light.Grey, "home"), (edition.Light, edition.Value));
        Assert.Equal("Windows 11 Home 26H2 · build 26300.7019 · supported, unmanaged", edition.Words);
    }

    [Fact]
    public void SmartAppControlEvaluatingOnAnUnsignedBuildIsAmberAndOffOrSignedHasNoRow()
    {
        var home = On("Core", 0x65, "25H2", 26200);
        var evaluating = Row(MachineRows.Build(home with { SmartAppControl = 2, Signed = false }, AfterHome24H2), "sac");
        Assert.NotNull(evaluating);
        Assert.Equal((Light.Amber, "evaluation", MachineRows.SmartAppControlWords), (evaluating.Light, evaluating.Value, evaluating.Words));
        Assert.Null(Row(MachineRows.Build(home with { SmartAppControl = 0 }, AfterHome24H2), "sac"));
        Assert.Null(Row(MachineRows.Build(home with { SmartAppControl = 2, Signed = true }, AfterHome24H2), "sac"));
        // Enforcing has no row: the unsigned engine that would raise it cannot start.
        Assert.Null(Row(MachineRows.Build(home with { SmartAppControl = 1 }, AfterHome24H2), "sac"));
    }

    [Fact]
    public void AProjectorOutputOnThePrimaryDisplayIsAmber()
    {
        var os = EditionTable.Classify("Core", 0x65, "25H2", 26200, 1);
        var locked = LockedDriverSet.Of(NodeIdentity.Unread, 1);
        var rows = MachineRows.Engine(os, locked, locked, [("Stage left", @"\\.\DISPLAY1")]);
        var row = Assert.Single(rows, r => r.Item == "display.primary:Stage left");
        Assert.Equal((Light.Amber, @"\\.\DISPLAY1"), (row.Light, row.Value));
        Assert.Equal("notifications, UAC and setup screens open on Stage left: make the desk's monitor primary", row.Words);
        Assert.DoesNotContain(MachineRows.Engine(os, locked, locked, []), r => r.Item.StartsWith("display.primary", StringComparison.Ordinal));
    }

    [Fact]
    public void ANetworkOnPublicWithNoRuleIsAmber()
    {
        var home = On("Core", 0x65, "25H2", 26200) with
        {
            NetworksRead = true,
            Networks = [new NetworkProfile("Show LAN", "public", false), new NetworkProfile("Office", "private", false), new NetworkProfile("Venue", "public", true)],
        };
        var rows = MachineRows.Build(home, AfterHome24H2);
        var row = Assert.Single(rows, r => r.Item.StartsWith("network.public:", StringComparison.Ordinal));
        Assert.Equal(("network.public:Show LAN", Light.Amber), (row.Item, row.Light));
        Assert.Contains("Settings ▸ Network & internet ▸ Show LAN ▸ Network profile type", row.Words, StringComparison.Ordinal);
        // The node names no show adapter: the row says what to do if show traffic crosses that network, never that it does.
        Assert.Contains("if show traffic crosses it, make it Private", row.Words, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Professional", 0x30u)]
    [InlineData("ProfessionalWorkstation", 0xA1u)]
    public void ProAndWorkstationHaveTheKiosksInformationRowAndNoneOfHomesOthers(string edition, uint product)
    {
        var rows = MachineRows.Build(On(edition, product, "25H2", 26200), AfterHome24H2);
        var info = Assert.Single(rows, r => r.Value == MachineRows.Information);
        Assert.Equal(("edition.kiosk", Light.Grey, (string?)null), (info.Item, info.Light, info.Fix));
        Assert.Equal("no Shell Launcher on this edition: the supervisor starts the engine with Explorer underneath", info.Words);
        Assert.Equal("Kiosk on this edition", MachineRows.Label(info.Item));
    }

    [Theory]
    [InlineData("Enterprise", 0x04u)]
    [InlineData("EnterpriseS", 0x7Du)]
    [InlineData("Education", 0x79u)]
    [InlineData("IoTEnterprise", 0xBCu)]
    [InlineData("IoTEnterpriseS", 0xBFu)]
    public void EnterpriseEducationAndIotHaveNoEditionRow(string edition, uint product)
    {
        var rows = MachineRows.Build(On(edition, product, "25H2", 26200), AfterHome24H2);
        Assert.DoesNotContain(rows, r => r.Item.StartsWith("edition.", StringComparison.Ordinal));
    }

    [Fact]
    public void Home24H2PastItsServicingIsAmberAndAVersionTheTableLacksIsGrey()
    {
        var home24 = Row(MachineRows.Build(On("Core", 0x65, "24H2", 26100), AfterHome24H2), "os.servicing");
        Assert.NotNull(home24);
        Assert.Equal((Light.Amber, "ended"), (home24.Light, home24.Value));
        Assert.Equal("Windows 11 Home 24H2 left servicing on 13 Oct 2026: this node gets no security updates — move to 25H2 or later", home24.Words);
        // On the day itself it is still serviced.
        Assert.Null(Row(MachineRows.Build(On("Core", 0x65, "24H2", 26100), new DateOnly(2026, 10, 13)), "os.servicing"));
        Assert.Null(Row(MachineRows.Build(On("Core", 0x65, "25H2", 26200), AfterHome24H2), "os.servicing"));
        // Enterprise 24H2 is serviced a year longer.
        Assert.Null(Row(MachineRows.Build(On("Enterprise", 0x04, "24H2", 26100), AfterHome24H2), "os.servicing"));
        var unknown = Row(MachineRows.Build(On("Core", 0x65, "27H2", 28000), AfterHome24H2), "os.servicing");
        Assert.NotNull(unknown);
        Assert.Equal((Light.Grey, "unknown"), (unknown.Light, unknown.Value));
        Assert.StartsWith("servicing unknown", unknown.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void IotEnterpriseLtscHasNoneOfHomesRows()
    {
        var rows = MachineRows.Build(On("IoTEnterpriseS", 0xBF, "24H2", 26100), AfterHome24H2);
        Assert.DoesNotContain(rows, r => r.Item.StartsWith("edition.", StringComparison.Ordinal));
        Assert.Null(Row(rows, "os.servicing"));
        Assert.Equal("ltsc", Row(rows, "os.edition")!.Value);
        Assert.Equal(new DateOnly(2034, 10, 10), ServicingTable.EndOf(EditionTable.Classify("IoTEnterpriseS", 0, "24H2", 26100, 1)));
    }
}
