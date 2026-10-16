using System.Text.Json;
using PlayMap.Assistant;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>INDEX's scan section lists each surface suggestion waiting for a person under its scan (the scan's id as its parent), its
/// id the proposal that approves it; once the suggestion is decided its row is gone.</summary>
public sealed class ScanSuggestIndexTests
{
    private static JsonElement[] Rows(OpsRig rig)
    {
        var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "scan");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        return [.. JsonDocument.Parse(ms.ToArray()).RootElement.EnumerateArray().Select(r => r.Clone())];
    }

    [Fact]
    public async Task EachWaitingSuggestionIsARowUnderItsScan()
    {
        var rig = new OpsRig(new AssistantSettings { Enabled = true }, project: MultiViewScanKit.Project());
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}");
            var suggestions = JsonDocument.Parse(await rig.Ok("SCAN SUGGEST " + id)).RootElement.GetProperty("suggestions").EnumerateArray().ToList();
            Assert.NotEmpty(suggestions);
            var rows = Rows(rig).Where(r => r.GetProperty("kind").GetString() == "suggestion").ToList();
            Assert.Equal(suggestions.Select(s => s.GetProperty("proposal").GetString()), rows.Select(r => r.GetProperty("id").GetString()));
            Assert.All(rows, r =>
            {
                Assert.Equal(id, r.GetProperty("parent").GetString());
                Assert.Equal("amber", r.GetProperty("light").GetString());
            });

            // Decided, a suggestion's row is gone.
            var first = suggestions[0].GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!).ToList();
            foreach (var p in first)
                await rig.Ok("REJECT " + p);
            Assert.All(first, p => Assert.Equal(ProposalState.Rejected, rig.Assistant.Queue.Find(p)!.State));
            Assert.DoesNotContain(Rows(rig), r => r.GetProperty("id").GetString() == first[0]);
            Assert.Equal(rows.Count - 1, Rows(rig).Count(r => r.GetProperty("kind").GetString() == "suggestion"));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
