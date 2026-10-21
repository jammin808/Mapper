using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Install;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Install;

/// <summary>STATE <c>machine.install</c> and preflight's <c>install</c> section. tests/fixtures/state/install.json is the block from a
/// supervised install read through the engine (written with PLAYMAP_WRITE_FIXTURES=1, else compared), install.cards.json the cards the
/// desk draws from it; the preflight rows are asserted by their fields with each fault injected: a folder run on a show node, a
/// supervisor that stopped trying, auto-logon off, each edition family the machine platform reads, the freeze rule waiting.</summary>
public sealed class InstallStateFixtureTest
{
    private static readonly SupervisorStatus Running = new("running", 4120, 0, "",
        "active: open outputs' shown frames may stand still 3 s while the GPU runs", 1_760_000_000_000);

    private static readonly InstallFacts Supervised = new(true, "0.2.17", @"C:\Program Files\PlayMap", true, @"SHOWNODE\show", Running, true, "show",
        false);

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Install",
            ["path"] = "machine.install",
            ["light"] = "light",
            ["words"] = "{words}",
            ["fields"] = new JsonArray(Field("Installed", "installed"), Field("Version", "msi"), Field("Folder", "dir"), Field("Supervised", "supervised"),
                Field("Show user", "showUser"), Field("Signed", "signed")),
            ["actions"] = new JsonArray("PREFLIGHT QUICK", "HEALTH"),
        },
        new JsonObject
        {
            ["title"] = "Supervisor",
            ["path"] = "machine.install.supervisor",
            ["light"] = new JsonObject
            {
                ["path"] = "state",
                ["red"] = new JsonArray("stopped-too-many-restarts"),
                ["amber"] = new JsonArray("waiting-for-session", "backoff", "held", "stopping"),
                ["green"] = new JsonArray("running", "starting"),
            },
            ["words"] = "{state} · {restarts} restarts in ten minutes{lastCause? · {lastCause}:}",
            ["fields"] = new JsonArray(Field("State", "state"), Field("Engine pid", "pid"), Field("Restarts", "restarts"), Field("Last cause", "lastCause"),
                Field("Freeze rule", "freezeRule"), Field("At", "at")),
            ["actions"] = new JsonArray("PREFLIGHT QUICK"),
        },
        new JsonObject
        {
            ["title"] = "Auto-logon",
            ["path"] = "machine.install.autoLogon",
            ["light"] = new JsonArray(new JsonObject { ["if"] = "on", ["light"] = "green" }, new JsonObject { ["light"] = "amber" }),
            ["words"] = "{on?signs in {user}:off}",
            ["fields"] = new JsonArray(Field("On", "on"), Field("User", "user")),
            ["actions"] = new JsonArray("PREFLIGHT QUICK"),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
            node = node is JsonObject o ? o[part] : null;
        return node;
    }

    [Fact]
    public async Task TheInstallBlockIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = new OpsRig();
        rig.Engine.InstallSource = () => Supervised;
        rig.Engine.InstallReadAgain();
        var install = rig.State().GetProperty("machine").GetProperty("install");
        Assert.True(install.GetProperty("installed").GetBoolean());
        Assert.Equal("0.2.17", install.GetProperty("msi").GetString());
        Assert.Equal("running", install.GetProperty("supervisor").GetProperty("state").GetString());
        // The block's supervisor is the status file's record as the supervisor writes it.
        Assert.Equal(Running, SupervisorStatus.Parse(install.GetProperty("supervisor").GetRawText()));
        Assert.Equal("green", install.GetProperty("light").GetString());
        Assert.False(install.GetProperty("signed").GetBoolean());
        // Auto-logon names its user and nothing else: no password key anywhere in the block.
        Assert.Equal(["on", "user"], install.GetProperty("autoLogon").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("assword", install.GetRawText(), StringComparison.Ordinal);

        var fixture = new JsonObject { ["machine"] = new JsonObject { ["install"] = JsonNode.Parse(install.GetRawText()) } }
            .ToJsonString(FixtureJson.Indented) + "\n";
        var cards = Cards().ToJsonString(FixtureJson.Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("install.json"), fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("install.cards.json"), cards);
        }
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("install.json")).ReplaceLineEndings("\n"), fixture);
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("install.cards.json")).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(at.AsObject().ContainsKey(field!["path"]!.GetValue<string>()), $"{card["title"]}: {field["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                var text = action!.GetValue<string>();
                Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }

    [Fact]
    public async Task AFolderRunSaysSoInStateAndIsNoInstall()
    {
        await using var rig = new OpsRig();
        rig.Engine.InstallSource = () => InstallFacts.NotInstalled;
        rig.Engine.InstallReadAgain();
        var install = rig.State().GetProperty("machine").GetProperty("install");
        Assert.False(install.GetProperty("installed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, install.GetProperty("supervisor").ValueKind);
        Assert.Equal("grey", install.GetProperty("light").GetString());
        Assert.Equal("not installed: a folder run, which nothing restarts if it stops", install.GetProperty("words").GetString());
    }

    [Fact]
    public async Task TheInstallSectionReachesPreflightsReport()
    {
        await using var rig = new OpsRig(services: s => s with { PreflightChecks = [.. s.PreflightChecks, new InstallPreflight()] });
        rig.Engine.InstallSource = () => Supervised with { Supervisor = Running with { State = "stopped-too-many-restarts", LastCause = "the engine exited" } };
        rig.Engine.InstallReadAgain();
        var report = PreflightReports.Read(await rig.Ok("PREFLIGHT QUICK WAIT"))!;
        var supervisor = Assert.Single(report.Rows, r => r.Id == "install:supervisor");
        Assert.Equal("install", supervisor.Section);
        Assert.Equal(Light.Red, supervisor.Light);
        Assert.Equal("stopped", supervisor.Value);
        Assert.Contains("PlayMap.Supervisor.exe resume", supervisor.Note, StringComparison.Ordinal);
        Assert.Contains(report.Rows, r => r is { Id: "install:signed", Light: Light.Grey, Value: "i" });
    }

    [Fact]
    public void AFolderRunIsAmberOnAShowNodeAndGreyElsewhere()
    {
        var show = Assert.Single(InstallPreflight.Rows(InstallFacts.NotInstalled, "n1", showNode: true, EditionFamily.Pro), r => r.Id == "install:installed");
        Assert.Equal((Light.Amber, "folder run"), (show.Light, show.Value));
        Assert.StartsWith("not installed: nothing restarts the engine if it stops", show.Note, StringComparison.Ordinal);
        var desk = Assert.Single(InstallPreflight.Rows(InstallFacts.NotInstalled, "n1", showNode: false, EditionFamily.Pro), r => r.Id == "install:installed");
        Assert.Equal(Light.Grey, desk.Light);
    }

    [Fact]
    public void ASupervisedInstallSaysItsSupervisorAndAutoLogon()
    {
        var rows = InstallPreflight.Rows(Supervised, "n1", showNode: true, EditionFamily.Pro);
        Assert.Equal(["install:installed", "install:supervisor", "install:autologon", "install:policies", "install:signed"], rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.Equal("install", r.Section));
        Assert.Equal((Light.Green, "running"), (rows[1].Light, rows[1].Value));
        Assert.Equal((Light.Green, "on", "auto-logon signs in show"), (rows[2].Light, rows[2].Value, rows[2].Note));

        var off = InstallPreflight.Rows(Supervised with { AutoLogon = false, AutoLogonUser = "" }, "n1", true, EditionFamily.Pro).Single(r => r.Id == "install:autologon");
        Assert.Equal((Light.Amber, "off"), (off.Light, off.Value));

        var waiting = InstallPreflight.Rows(Supervised with { Supervisor = Running with { FreezeRule = "the engine reports no shown frames: the freeze rule waits for them" } },
            "n1", true, EditionFamily.Pro).Single(r => r.Id == "install:supervisor");
        Assert.Equal((Light.Grey, "i"), (waiting.Light, waiting.Value));
        Assert.EndsWith("the freeze rule waits for them", waiting.Note, StringComparison.Ordinal);

        var held = InstallPreflight.Rows(Supervised with { Supervisor = Running with { State = "held" } }, "n1", true, EditionFamily.Pro).Single(r => r.Id == "install:supervisor");
        Assert.Equal((Light.Amber, "held"), (held.Light, held.Value));

        var none = InstallPreflight.Rows(Supervised with { Supervisor = null }, "n1", true, EditionFamily.Pro).Single(r => r.Id == "install:supervisor");
        Assert.Equal((Light.Amber, "no status"), (none.Light, none.Value));
    }

    [Fact]
    public void TheBlocksLightTheSupervisorCardsLightAndThePreflightRowAgreeInEveryState()
    {
        // The Supervisor card's descriptor lists every state the supervisor writes under its light; the block's own light and the
        // install:supervisor row light each state the same (a supervised node with auto-logon on, the freeze rule active).
        var light = Cards().Single(c => c!["title"]!.GetValue<string>() == "Supervisor")!["light"]!.AsObject();
        var states = new List<(string State, string Light)>();
        foreach (var word in new[] { "red", "amber", "green" })
            states.AddRange(light[word]!.AsArray().Select(s => (s!.GetValue<string>(), word)));
        Assert.Equal(7, states.Count);
        foreach (var (state, card) in states)
        {
            var facts = Supervised with { Supervisor = Running with { State = state } };
            Assert.True(card == facts.Light, $"{state}: the card is {card}, the block {facts.Light}");
            var row = InstallPreflight.Rows(facts, "n1", true, EditionFamily.Pro).Single(r => r.Id == "install:supervisor");
            Assert.True(card == row.Light.ToString().ToLowerInvariant(), $"{state}: the card is {card}, the preflight row {row.Light}");
        }
    }

    [Fact]
    public void ThePoliciesRowTakesTheEditionTheMachinePlatformReadsAndNamesTheMachineSectionsUpdatesRow()
    {
        // The edition is the machine platform's reading, the same on a folder run as on an install: Home is told it honours no
        // Update policy, as machine preflight's edition.updates row says; every other edition is pointed at the updates row, which
        // says whether a policy holds updates back, and is never told to defer them by hand while that row may say they are held.
        foreach (var facts in new[] { InstallFacts.NotInstalled, Supervised })
        {
            var home = InstallPreflight.Rows(facts, "n1", true, EditionFamily.Home).Single(r => r.Id == "install:policies");
            Assert.Equal((Light.Grey, "i"), (home.Light, home.Value));
            Assert.StartsWith("not on this edition: Home honours no Update policy", home.Note, StringComparison.Ordinal);
            Assert.Contains("edition.updates", home.Note, StringComparison.Ordinal);
            foreach (var managed in new[] { EditionFamily.Pro, EditionFamily.Workstation, EditionFamily.Enterprise, EditionFamily.Education, EditionFamily.Iot })
            {
                var row = InstallPreflight.Rows(facts, "n1", true, managed).Single(r => r.Id == "install:policies");
                Assert.Equal((Light.Grey, "i"), (row.Light, row.Value));
                Assert.StartsWith("this build writes no Windows Update policy", row.Note, StringComparison.Ordinal);
                Assert.Contains("updates row", row.Note, StringComparison.Ordinal);
                Assert.DoesNotContain("defer", row.Note, StringComparison.Ordinal);
            }
            var unread = InstallPreflight.Rows(facts, "n1", true, EditionFamily.Unknown).Single(r => r.Id == "install:policies");
            Assert.Contains("no Windows edition is read on this node", unread.Note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheSectionReadsTheEditionFromTheMachinePlatformAndTheBlockCarriesNone()
    {
        await using var rig = new OpsRig(services: s => s with { PreflightChecks = [.. s.PreflightChecks, new InstallPreflight(_ => InstallFacts.NotInstalled)] });
        var report = PreflightReports.Read(await rig.Ok("PREFLIGHT QUICK WAIT"))!;
        var policies = Assert.Single(report.Rows, r => r.Id == "install:policies");
        Assert.Equal(InstallPreflight.PoliciesWords(rig.Engine.Services.MachinePlatform.Identity().Os.Family), policies.Note);
        rig.Engine.InstallSource = () => Supervised;
        rig.Engine.InstallReadAgain();
        Assert.False(rig.State().GetProperty("machine").GetProperty("install").TryGetProperty("edition", out _));
    }

    [Fact]
    public void TheStatusFileTheSupervisorWritesIsReadBackAndAnythingElseIsNot()
    {
        var text = "{\"state\":\"running\",\"pid\":4120,\"restarts\":2,\"lastCause\":\"the engine exited\",\"freezeRule\":\"active\",\"at\":1760000000000}";
        Assert.Equal(new SupervisorStatus("running", 4120, 2, "the engine exited", "active", 1_760_000_000_000), SupervisorStatus.Parse(text));
        Assert.Null(SupervisorStatus.Parse("{\"state\":\"running\"}"));
        Assert.Null(SupervisorStatus.Parse("nonsense"));
        Assert.Null(SupervisorStatus.Parse(null));
        // The engine reads the file by the supervisor's own record, so it refuses what the supervisor would: a negative pid or count.
        Assert.Null(SupervisorStatus.Parse(text.Replace("\"pid\":4120", "\"pid\":-4120", StringComparison.Ordinal)));
        Assert.Null(SupervisorStatus.Parse(text.Replace("\"restarts\":2", "\"restarts\":-2", StringComparison.Ordinal)));
    }

    [Fact]
    public void ATestMachineIsAFolderRun()
    {
        // No test machine holds the install's key: Linux has none, and the Windows test job installs nothing.
        var facts = InstallFacts.ReadThisMachine();
        Assert.False(facts.Installed);
        Assert.Null(facts.Supervisor);
        Assert.Equal("", facts.Version);
    }
}
