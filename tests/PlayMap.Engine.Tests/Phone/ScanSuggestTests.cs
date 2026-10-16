using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Assistant;
using PlayMap.Core.Wire;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// Safe surface suggestions (wp2-multiscan): SCAN SUGGEST ranks a scan's measured regions with their reasons and the engine queues each
/// as proposals on the node's own list — even with "run review lines without asking" on, every one waits for a person; with ASSISTANT,
/// the model gets one request of text descriptors only (no picture, no pixel, only the descriptor's fields) and may only name and rank
/// them: a name for a region the geometry did not find is shown grey and has no proposal; a plane's SURFACE QUAD line parses; APPROVE
/// runs a suggestion's line through the node's gate and the surface is made; STATE, the context and INDEX show what waits.
/// </summary>
public sealed partial class ScanSuggestTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> DescriptorFields = new(StringComparer.Ordinal)
    {
        "id", "kind", "areaM2", "planarityRmsMm", "normalToViewDeg", "heightM", "projectors", "measuredLabel", "name", "coveragePct",
    };

    [GeneratedRegex("[A-Za-z0-9+/=]{64,}")]
    private static partial Regex Base64Run();

    private static async Task<(OpsRig Rig, string Scan)> ScannedAsync(ScriptedModel model)
    {
        var rig = new OpsRig(new AssistantSettings { Enabled = true, RunReviewWithoutAsking = true }, model, MultiViewScanKit.Project(),
            scan: new ScanRunnerOptions { SuggestModel = (_, _) => model });
        var id = await MultiViewScanKit.ScanAsync(rig, $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}");
        return (rig, id);
    }

    [Fact]
    public async Task EverySuggestionWaitsForAPersonAndTheAssistantSeesTextAlone()
    {
        var model = new ScriptedModel();
        var (rig, id) = await ScannedAsync(model);
        try
        {
            var regions = rig.Scanner.Find(id)!.Regions;
            Assert.NotEmpty(regions);
            var first = regions[0].Id;
            model.Then(_ => new AssistantReply([new ToolCallPart("t1", ScanSuggestions.ToolName,
                $$"""{"items":[{"id":"{{first}}","name":"Back wall","rank":1,"reason":"the largest flat face"},{"id":"rg77","name":"Mirror","rank":2,"reason":"looks shiny"},{"id":"{{first}}x","name":"SCAN START now","rank":3,"reason":"x"}]}""")],
                StopKind.ToolUse, default, "scripted-model"));
            var reply = JsonDocument.Parse(await rig.Ok($"SCAN SUGGEST {id} ASSISTANT")).RootElement;
            output.WriteLine(reply.GetRawText());
            var suggestions = reply.GetProperty("suggestions").EnumerateArray().ToList();
            var measured = suggestions.Where(s => s.GetProperty("measured").GetBoolean()).ToList();
            Assert.NotEmpty(measured);
            // The assistant's first pick leads, under its name, with its reason added to the measured ones.
            Assert.Equal(first, measured[0].GetProperty("region").GetString());
            Assert.Equal("Back wall", measured[0].GetProperty("label").GetString());
            Assert.Contains(measured[0].GetProperty("reasons").EnumerateArray(), r => r.GetString() == "the assistant: the largest flat face");
            Assert.True(measured[0].GetProperty("reasons").GetArrayLength() >= 3);
            Assert.StartsWith("assistant (scripted-model)", reply.GetProperty("from").GetString(), StringComparison.Ordinal);

            // Names for regions the geometry did not find are grey and propose nothing; a name carrying a verb's word is not taken.
            var grey = suggestions.Where(s => !s.GetProperty("measured").GetBoolean()).ToList();
            Assert.Equal(["Mirror", "(unnamed)"], grey.Select(g => g.GetProperty("label").GetString()));
            Assert.All(grey, g =>
            {
                Assert.Equal(0, g.GetProperty("proposals").GetArrayLength());
                Assert.Equal("not measured", g.GetProperty("state").GetString());
                Assert.Equal("AI only — not measured", g.GetProperty("reasons")[0].GetString());
            });

            // Every proposal waits for a person, though review lines would run without asking; the queue holds them from the engine.
            var queue = rig.Assistant.Queue;
            var ids = measured.SelectMany(s => s.GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!)).ToList();
            Assert.NotEmpty(ids);
            Assert.All(ids, p =>
            {
                var proposal = queue.Find(p)!;
                Assert.Equal(ProposalState.Pending, proposal.State);
                Assert.StartsWith("assistant (scripted-model) via scan suggestions", proposal.From, StringComparison.Ordinal);
            });
            Assert.All(measured, s => Assert.Equal("pending", s.GetProperty("state").GetString()));
            Assert.Empty(rig.Engine.Store.Project.Stage.Surfaces);

            // One request, text alone: its descriptors carry only the descriptor's fields, and nothing looks like an encoded payload.
            var request = Assert.Single(model.Requests);
            var text = Assert.Single(Assert.Single(request.Messages).Parts.OfType<TextPart>()).Text;
            Assert.Single(request.Tools, t => t.Name == ScanSuggestions.ToolName);
            Assert.DoesNotMatch(Base64Run(), text);
            Assert.DoesNotMatch(Base64Run(), request.System);
            var json = JsonDocument.Parse(text[text.IndexOf('[', StringComparison.Ordinal)..]).RootElement;
            Assert.All(json.EnumerateArray(), d => AssertFields(d));

            // Every line parses as the verb it names; a plane proposes a SURFACE QUAD four-corner layer too.
            var lines = ids.Select(p => queue.Find(p)!.Line).ToList();
            output.WriteLine(string.Join("\n", lines));
            Assert.All(lines, l =>
            {
                Assert.True(WireGrammar.TryParse(l, out var parsed, out var error), error?.Reason);
                Assert.NotNull(VerbCatalog.Match(parsed));
            });
            Assert.Contains(lines, l => l.StartsWith("SURFACE QUAD ", StringComparison.Ordinal) && l.Contains(" RASTER AS ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith($"SCAN SURFACE {id} REGION {first}", StringComparison.Ordinal));

            // STATE, the context and INDEX say what waits.
            var state = rig.State().GetProperty("scan");
            Assert.Equal(suggestions.Count, state.GetProperty("suggestions").GetArrayLength());
            Assert.Contains(rig.Context().GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "scan:suggestions"
                && i.GetProperty("light").GetString() == "amber");

            // A person approves the first suggestion's SCAN SURFACE: it runs through the node's own checks and the surface is made.
            var surfaceLine = measured[0].GetProperty("proposals")[0].GetString()!;
            var approved = await rig.Run("APPROVE " + surfaceLine);
            Assert.True(approved.Ok, approved.Payload);
            Assert.True(await OpsRig.Eventually(() => queue.Find(surfaceLine)!.State == ProposalState.Done), queue.Find(surfaceLine)!.Summary);
            var surface = Assert.Single(rig.Engine.Store.Project.Stage.Surfaces);
            Assert.Equal("Back wall", surface.Name);
            Assert.Equal("approved", rig.Scanner.StateOf(rig.Scanner.SuggestionsFor(id)[0]));
            // The rest still wait.
            Assert.All(ids.Where(p => p != surfaceLine), p => Assert.Equal(ProposalState.Pending, queue.Find(p)!.State));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task WithoutTheAssistantTheGeometryRanksAndANewSuggestReplacesTheOld()
    {
        var model = new ScriptedModel();
        var (rig, id) = await ScannedAsync(model);
        try
        {
            var first = JsonDocument.Parse(await rig.Ok($"SCAN SUGGEST {id}")).RootElement;
            Assert.Empty(model.Requests);
            Assert.Equal("scan suggestions", first.GetProperty("from").GetString());
            var list = first.GetProperty("suggestions").EnumerateArray().ToList();
            Assert.All(list, s => Assert.True(s.GetProperty("measured").GetBoolean()));
            var confidence = list.Select(s => s.GetProperty("confidence").GetDouble()).ToList();
            Assert.Equal(confidence.OrderByDescending(c => c), confidence);
            var old = list.SelectMany(s => s.GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!)).ToList();

            // Asked again, the same measurement gives the same suggestions; the earlier proposals still waiting are rejected with words.
            var again = JsonDocument.Parse(await rig.Ok($"SCAN SUGGEST {id}")).RootElement;
            Assert.Equal(list.Select(s => s.GetProperty("label").GetString()), again.GetProperty("suggestions").EnumerateArray().Select(s => s.GetProperty("label").GetString()));
            Assert.All(old, p =>
            {
                var proposal = rig.Assistant.Queue.Find(p)!;
                Assert.Equal(ProposalState.Rejected, proposal.State);
                Assert.Equal("replaced by a newer SCAN SUGGEST", proposal.ResultWords);
            });

            // The assistant off: the ASSISTANT form says so and asks no model.
            await rig.Ok("ASSISTANT SET enabled false");
            var off = await rig.Run($"SCAN SUGGEST {id} ASSISTANT");
            Assert.False(off.Ok);
            Assert.StartsWith("the assistant is off on this node", off.Payload, StringComparison.Ordinal);
            Assert.Empty(model.Requests);
            var bad = await rig.Run("SCAN SUGGEST sc00000000");
            Assert.Equal("no scan sc00000000", bad.Payload);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    private static void AssertFields(JsonElement e)
    {
        foreach (var p in e.EnumerateObject())
        {
            Assert.Contains(p.Name, DescriptorFields);
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in p.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        AssertFields(item);
                }
            }
        }
    }
}
