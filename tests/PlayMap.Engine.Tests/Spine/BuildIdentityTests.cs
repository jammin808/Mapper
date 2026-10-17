using System.Text.Json;
using System.Text.Encodings.Web;
using PlayMap.Core;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Fleet;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>STATE names the product version alone, so it is the same from every build of a release and the committed fixtures
/// hold on CI and on a desk alike; the build's whole identity (number and commit) is HEALTH's "build", which the context's machine
/// item carries to Patterns' Eye and STATE.fleet carries for each peer.</summary>
public sealed class BuildIdentityTests
{
    private static readonly BuildInfo Ci = new(BuildInfo.Current.Version, 4242, "fedcba9");
    private static readonly BuildInfo Local = new(BuildInfo.Current.Version, 0, "0a1b2c3");

    private static async Task<(string State, string HealthBuild, string MachineWords)> Node(BuildInfo build)
    {
        var services = HealthTests.Services(new ManualTime(), sampler: new HealthTests.FakeSampler()) with { Build = build };
        await using var engine = new EngineCore(services, Project.CreateDefault("Show"));
        Assert.True(WireGrammar.TryParse("HEALTH", out var line, out var error), error?.Reason);
        var health = await engine.HandleAsync(line, SessionInfo.Desk("test"), CancellationToken.None);
        Assert.True(health.Ok, health.Payload);
        using var record = JsonDocument.Parse(health.Payload!);
        var machine = ContextBuilder.Build(engine).Items.Single(i => i.Id == "machine");
        return (MainStateFixtureTests.Normalise(engine.State().Json), record.RootElement.GetProperty("build").GetString()!, machine.State);
    }

    [Fact]
    public async Task StateIsTheSameFromACiBuildAndALocalOneWhileHealthTellsThemApart()
    {
        var ci = await Node(Ci);
        var local = await Node(Local);

        Assert.Equal(ci.State, local.State);
        using (var state = JsonDocument.Parse(ci.State))
            Assert.Equal(BuildInfo.Current.Version, state.RootElement.GetProperty("version").GetString());
        foreach (var said in new[] { "fedcba9", "0a1b2c3" })
            Assert.DoesNotContain(said, ci.State, StringComparison.Ordinal);

        Assert.Equal(BuildInfo.Current.Version + "+4242.fedcba9", ci.HealthBuild);
        Assert.Equal(BuildInfo.Current.Version + "-dev+0a1b2c3", local.HealthBuild);
        Assert.Contains("build " + Ci.Informational, ci.MachineWords, StringComparison.Ordinal);
        Assert.Contains("build " + Local.Informational, local.MachineWords, StringComparison.Ordinal);
    }

    [Fact]
    public void ANodeReportsTheBuildItRunsUnlessTold()
    {
        Assert.Same(BuildInfo.Current, EngineServices.ForTests().Build);
    }

    [Fact]
    public void EveryCommittedStateFixtureNamesTheProductVersionAndNothingOfThisBuild()
    {
        var root = NodeExe.RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "tests", "fixtures", "state"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(root, "integrations", "companion-module-playmap", "test", "fixtures"), "*.json"))
            .ToList();
        var withVersion = 0;
        var current = BuildInfo.Current;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var version))
            {
                Assert.True(current.Version == version.GetString(), $"{Path.GetFileName(file)} says version {version}, the product is {current.Version}");
                withVersion++;
            }
            // The build's own identity — "-dev", its number, its commit — never reaches a fixture, in either of JSON's spellings of "+".
            foreach (var form in new[] { current.Informational, JsonEncodedText.Encode(current.Informational, JavaScriptEncoder.Default).ToString() })
                Assert.False(text.Contains(form, StringComparison.Ordinal), $"{Path.GetFileName(file)} carries this build's {form}");
        }
        Assert.True(withVersion >= 4, $"only {withVersion} whole-STATE fixtures were found");
    }
}
