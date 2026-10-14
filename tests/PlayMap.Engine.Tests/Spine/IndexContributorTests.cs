using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>A package's INDEX section is data: a contributor is listed once under its section, a second one for the same section
/// throws when the routes are built, and its rows come from published state on any thread.</summary>
public sealed class IndexContributorTests
{
    private sealed class FakeSection(string section) : IIndexContributor
    {
        public string Section => section;

        public IndexPresetHint Hint { get; } = new("Effects", "fx", [new IndexKey("Bypass", "FX BYPASS $(id) TOGGLE", "fx.bypassed", "instant")]);

        public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
        {
            w.WriteStartArray();
            foreach (var o in project.Outputs)
            {
                w.WriteStartObject();
                w.WriteString("id", o.Id);
                w.WriteString("kind", "output");
                w.WriteString("label", o.Name);
                w.WriteString("parent", "");
                w.WriteString("light", "grey");
                w.WriteString("state", snapshot.Air.Blackout ? "black" : "show");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }

    [Fact]
    public async Task ASectionIsListedOnceAndASecondContributorForItThrows()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        // Section names no package uses, so the test holds whatever the merged packages contribute.
        var first = new FakeSection("spine-test-a");
        engine.Routes.Index(first);
        engine.Routes.Index(new FakeSection("spine-test-b"));

        Assert.Equal(["spine-test-a", "spine-test-b"],
            engine.Routes.IndexSections.Select(s => s.Section).Where(s => s.StartsWith("spine-test-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Single(engine.Routes.IndexSections, s => s.Section == "spine-test-a");
        var twice = Assert.Throws<InvalidOperationException>(() => engine.Routes.Index(new FakeSection("spine-test-a")));
        Assert.Contains("spine-test-a", twice.Message, StringComparison.Ordinal);
        Assert.Same(first, engine.Routes.IndexSections.Single(s => s.Section == "spine-test-a"));
    }

    [Fact]
    public async Task RowsAreWrittenFromPublishedStateOffTheLane()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        engine.Routes.Index(new FakeSection("spine-test-rows"));
        var section = engine.Routes.IndexSections.Single(s => s.Section == "spine-test-rows");
        var snapshot = engine.Snapshot;

        var json = await Task.Run(() =>
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
                section.WriteRows(snapshot.Project.Project, snapshot, w);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        });

        using var doc = JsonDocument.Parse(json);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("op_1", row.GetProperty("id").GetString());
        Assert.Equal("show", row.GetProperty("state").GetString());
        Assert.Equal(new IndexKey("Bypass", "FX BYPASS $(id) TOGGLE", "fx.bypassed", "instant"), Assert.Single(section.Hint.Keys));
    }
}
