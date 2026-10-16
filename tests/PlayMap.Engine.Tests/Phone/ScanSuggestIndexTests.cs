using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Net;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>INDEX's suggestions section lists each surface suggestion waiting for a person under its scan (the scan's id as its parent),
/// its id the proposal that approves it, and its keys are the lines that decide it (APPROVE pressed twice, REJECT held); the scan
/// section's keys (the phones') list none; once the suggestion is decided its row is gone.</summary>
public sealed class ScanSuggestIndexTests
{
    private static JsonElement[] Rows(OpsRig rig, string name = "suggestions")
    {
        var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == name);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        return [.. JsonDocument.Parse(ms.ToArray()).RootElement.EnumerateArray().Select(r => r.Clone())];
    }

    /// <summary>The suggestion rows of the INDEX last pushed to a deck.</summary>
    private static List<string> Pushed(Companion.RecordingSink deck)
    {
        if (!deck.Latests.TryGetValue("index", out var line))
            return [];
        using var doc = JsonDocument.Parse(line["INDEX ".Length..]);
        return [.. doc.RootElement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "suggestions")
            .GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task EachWaitingSuggestionIsARowUnderItsScan()
    {
        var rig = new OpsRig(new AssistantSettings { Enabled = true }, project: MultiViewScanKit.Project());
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}");
            // A deck holding INDEX hears each suggestion come and go.
            var deck = new Companion.RecordingSink();
            Assert.True(((IPushTopics)rig.Engine).TryOpen("INDEX", [], Companion.CompanionRig.Paired, deck, out var watching, out var why), why);
            using var subscription = watching;
            var suggestions = JsonDocument.Parse(await rig.Ok("SCAN SUGGEST " + id)).RootElement.GetProperty("suggestions").EnumerateArray().ToList();
            Assert.NotEmpty(suggestions);
            var rows = Rows(rig).Where(r => r.GetProperty("kind").GetString() == "suggestion").ToList();
            Assert.Equal(suggestions.Select(s => s.GetProperty("proposal").GetString()), rows.Select(r => r.GetProperty("id").GetString()));
            Assert.All(rows, r =>
            {
                Assert.Equal(id, r.GetProperty("parent").GetString());
                Assert.Equal("amber", r.GetProperty("light").GetString());
            });
            Assert.DoesNotContain(Rows(rig, "scan"), r => r.GetProperty("kind").GetString() == "suggestion");
            var hint = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "suggestions").Hint;
            Assert.Equal([("APPROVE $(id)", "double"), ("REJECT $(id)", "hold")], hint.Keys.Select(k => (k.DownLine, k.Press)));
            Assert.True(await Host.Eventually.TrueAsync(() => Pushed(deck).SequenceEqual(rows.Select(r => r.GetProperty("id").GetString()!))),
                string.Join(", ", Pushed(deck)));

            // Decided, a suggestion's row is gone.
            var first = suggestions[0].GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!).ToList();
            foreach (var p in first)
                await rig.Ok("REJECT " + p);
            Assert.All(first, p => Assert.Equal(ProposalState.Rejected, rig.Assistant.Queue.Find(p)!.State));
            Assert.DoesNotContain(Rows(rig), r => r.GetProperty("id").GetString() == first[0]);
            Assert.Equal(rows.Count - 1, Rows(rig).Count(r => r.GetProperty("kind").GetString() == "suggestion"));
            Assert.True(await Host.Eventually.TrueAsync(() => Pushed(deck).Count == rows.Count - 1 && !Pushed(deck).Contains(first[0])),
                string.Join(", ", Pushed(deck)));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
