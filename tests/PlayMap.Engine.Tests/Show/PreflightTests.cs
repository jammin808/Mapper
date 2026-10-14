using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Devices.Model;
using PlayMap.Engine.Show;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Preflight: each built-in check turns red (or amber) on an injected fault and green once it is fixed, with a fix line
/// the catalogue reads; the budgets hold on a manual clock (a check past its budget is a grey row, its show-ready step stays open
/// and the report is not complete); PREFLIGHT answers its id and publishes the report in STATE, the context and the journal;
/// REPORT round-trips, from memory and from the file kept beside the journal.</summary>
public sealed class PreflightTests
{
    /// <summary>Displays and media readiness a test sets.</summary>
    private sealed class Machine : IOutputBackend, IMediaBackend
    {
        public List<DisplayInfo> Screens { get; } = [];

        public MediaReadiness Media { get; set; } = new(true, true, true, true, "ready");

        public string RendererName => "test";

        public IReadOnlyList<DisplayInfo> Displays() => [.. Screens];

        public IReadOnlyList<OutputHealth> Health() => [];

        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("tests import nothing");

        public IVideoSource? Open(MediaItem item) => null;

        public MediaReadiness Readiness(MediaItem item) => Media;

        public string? PathForHash(string contentHash) => null;
    }

    /// <summary>A check that never finishes by itself (it ends only when cancelled).</summary>
    private sealed class Hanging(TimeSpan budget) : IPreflightCheck
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Section => "sync";

        public PreflightDepth MinDepth => PreflightDepth.Quick;

        public TimeSpan Budget => budget;

        /// <summary>Completes once the runner has started this check (its budget runs from then).</summary>
        public Task Started => _started.Task;

        public async Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    private static Project WithClip()
    {
        var p = ShowRig.OneScreen();
        var clip = new MediaItem("md00000001", "Intro", MediaKind.Video, "/shows/intro.mov", "", 4L << 30, MediaInfo.Unknown, []);
        return p with
        {
            Media = [clip],
            Playlists = [p.Playlists[0] with { Items = p.Playlists[0].Items.Add(PlaylistItem.For("it_d", SourceRef.Media(clip.Id), "Intro")) }],
            Outputs = [p.Outputs[0] with { Kind = OutputKind.Display, DisplayKey = "DP-1" }],
            Stage = p.Stage with { Devices = [new DeviceLink("dv00000001", "Box", "pjlink", "10.0.0.50", 0, "", "", "op_1", 0, false)] },
        };
    }

    private static async Task<IReadOnlyList<PreflightRow>> Check(IPreflightCheck check, EngineCore engine) =>
        await check.RunAsync(new PreflightContext(engine, PreflightDepth.Quick, engine.ShowNow.Armed), CancellationToken.None);

    private static void Parses(PreflightRow row)
    {
        Assert.NotNull(row.Fix);
        Assert.True(WireGrammar.TryParse(row.Fix, out var line, out var error), error?.Reason);
        Assert.NotNull(VerbCatalog.Match(line!));
        Assert.True(VerbSyntax.Fits(VerbCatalog.Match(line!)!.Value.Spec, line!, out var why), $"{row.Fix}: {why}");
    }

    [Fact]
    public async Task EachBuiltInCheckFailsOnAnInjectedFaultAndPassesOnceFixed()
    {
        var machine = new Machine();
        var folder = Directory.CreateTempSubdirectory("playmap-preflight-").FullName;
        try
        {
            await using var rig = new ShowRig(WithClip(), s => s with { Outputs = machine, Media = machine, ProjectFolder = folder });
            var e = rig.Engine;

            // Outputs: no display at all, then another display, then its own.
            var outputs = Assert.Single(await Check(new OutputsCheck(), e));
            Assert.Equal((Light.Red, "OUTPUT ENABLE op_1 OFF"), (outputs.Light, outputs.Fix));
            Parses(outputs);
            machine.Screens.Add(new DisplayInfo("DP-2", "Side", 0, 0, 1280, 720, 60, true));
            outputs = Assert.Single(await Check(new OutputsCheck(), e));
            Assert.Equal((Light.Red, "SET op_1 displayKey \"DP-2\""), (outputs.Light, outputs.Fix));
            Parses(outputs);
            machine.Screens.Add(new DisplayInfo("DP-1", "Main", 1280, 0, 1920, 1080, 60, false));
            outputs = Assert.Single(await Check(new OutputsCheck(), e));
            Assert.Equal((Light.Green, "Main"), (outputs.Light, outputs.Value));

            // Media: missing, then present but unreadable, then ready.
            machine.Media = new MediaReadiness(false, false, false, false, "missing");
            var media = Assert.Single(await Check(new MediaCheck(), e));
            Assert.Equal((Light.Red, "MEDIA FETCH md00000001"), (media.Light, media.Fix));
            Parses(media);
            machine.Media = new MediaReadiness(true, true, false, false, "the file does not decode");
            Assert.Equal(Light.Red, Assert.Single(await Check(new MediaCheck(), e)).Light);
            machine.Media = new MediaReadiness(true, true, true, true, "ready");
            Assert.Equal(Light.Green, Assert.Single(await Check(new MediaCheck(), e)).Light);
            // The look a SAFE panic shows is media the show uses too.
            var loop = new MediaItem("md00000002", "Safe loop", MediaKind.Video, "/shows/safe.mov", "", 1L << 20, MediaInfo.Unknown, []);
            await e.Lane.RunAsync(() => e.Store.Apply("a safe look", p => p with
            {
                Media = p.Media.Add(loop),
                Show = p.Show with { Panic = [new OutputPanicPlan("op_1", PanicOutcome.Safe, 1000, SourceRef.Media(loop.Id))] },
            }));
            machine.Media = new MediaReadiness(false, false, false, false, "missing");
            var safeLook = (await Check(new MediaCheck(), e)).Single(r => r.Id == "media:md00000002");
            Assert.Equal((Light.Red, "MEDIA FETCH md00000002"), (safeLook.Light, safeLook.Fix));
            machine.Media = new MediaReadiness(true, true, true, true, "ready");

            // Devices: unreachable, standby, shutter closed, ready.
            const string dv = "dv00000001";
            DeviceState? state = new() { LinkId = dv, Liveness = DeviceLiveness.Gone, Online = false };
            var devices = new DevicesCheck(_ => state);
            Assert.Equal(Light.Red, Assert.Single(await Check(devices, e)).Light);
            state = new DeviceState { LinkId = dv, Liveness = DeviceLiveness.Live, Online = true, Power = PowerState.Standby };
            var standby = Assert.Single(await Check(devices, e));
            Assert.Equal((Light.Red, $"DEVICE POWER {dv} ON"), (standby.Light, standby.Fix));
            Parses(standby);
            state = state with { Power = PowerState.On, ShutterClosed = true };
            var shut = Assert.Single(await Check(devices, e));
            Assert.Equal((Light.Amber, $"DEVICE SHUTTER {dv} OPEN"), (shut.Light, shut.Fix));
            Parses(shut);
            state = state with { ShutterClosed = false };
            Assert.Equal(Light.Green, Assert.Single(await Check(devices, e)).Light);

            // Disk: less than twice the largest file (4 GB), then under 10 GB, then plenty.
            long free = 6L << 30;
            var disk = new DiskCheck(_ => free);
            Assert.Equal(Light.Red, Assert.Single(await Check(disk, e)).Light);
            free = 9L << 30;
            Assert.Equal(Light.Amber, Assert.Single(await Check(disk, e)).Light);
            free = 500L << 30;
            Assert.Equal((Light.Green, "500.0 GB free"), (Assert.Single(await Check(disk, e)).Light, Assert.Single(await Check(disk, e)).Value));

            // Safety: a SAFE plan whose look is not in the show (a hand-edited file; REMOVE refuses a look in use), then a plan that
            // holds; armed is green.
            await e.Lane.RunAsync(() => e.Store.Apply("a plan from a file", p => p with
            {
                Show = p.Show with { Panic = [new OutputPanicPlan("op_1", PanicOutcome.Safe, 1000, SourceRef.Generator("gn_gone"))] },
            }));
            var unsafeRow = (await Check(new SafetyCheck(), e)).Single(r => r.Id == "safety:op_1");
            Assert.Equal((Light.Red, "PANIC SET op_1 BLACK"), (unsafeRow.Light, unsafeRow.Fix));
            Parses(unsafeRow);
            await rig.Ok("PANIC SET op_1 BLACK");
            Assert.DoesNotContain(await Check(new SafetyCheck(), e), r => r.Id == "safety:op_1");
            var rehearsal = (await Check(new SafetyCheck(), e)).Single(r => r.Id == "safety:armed");
            Assert.Equal((Light.Grey, "SHOW ARM"), (rehearsal.Light, rehearsal.Fix));
            Parses(rehearsal);
            await rig.Ok("SHOW ARM");
            Assert.Equal(Light.Green, (await Check(new SafetyCheck(), e)).Single(r => r.Id == "safety:armed").Light);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ABudgetHoldsOnTheManualClockAndTheReportIsPublished()
    {
        var machine = new Machine();
        machine.Screens.Add(new DisplayInfo("DP-1", "Main", 0, 0, 1920, 1080, 60, true));
        var hanging = new Hanging(TimeSpan.FromHours(1));
        var folder = Directory.CreateTempSubdirectory("playmap-preflight-").FullName;
        try
        {
            await using var rig = new ShowRig(WithClip(), s => s with
            {
                Outputs = machine,
                Media = machine,
                ProjectFolder = folder,
                JournalPath = Path.Combine(folder, "journal.jsonl"),
                PreflightChecks = [hanging],
            });
            var events = new List<ShowEvent>();
            rig.Engine.EventRaised += e =>
            {
                lock (events)
                    events.Add(e);
            };
            var reply = await rig.Run("PREFLIGHT");
            Assert.True(reply.Ok, reply.Payload);
            Assert.False(reply.Applied);
            var id = JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("id").GetString()!;
            Assert.StartsWith("pf", id, StringComparison.Ordinal);
            Assert.True(rig.Show().GetProperty("preflight").GetProperty("running").GetBoolean());
            Assert.StartsWith($"a preflight is running ({id})", await rig.Refused("PREFLIGHT QUICK"), StringComparison.Ordinal);
            Assert.Equal("no fleet fan-out on this node — PREFLIGHT checks this node only", await rig.Refused("PREFLIGHT ALL"));

            // The budget is an hour of the node's monotonic clock: only moving that clock ends the hanging check, so a runner that
            // timed it on real time would still be waiting when this test gives up.
            await hanging.Started.WaitAsync(TimeSpan.FromSeconds(30));
            rig.Advance(3_600_000);
            Assert.True(await ShowRig.Eventually(() => !rig.Show().GetProperty("preflight").GetProperty("running").GetBoolean()));

            var summary = rig.Show().GetProperty("preflight");
            Assert.Equal(id, summary.GetProperty("id").GetString());
            Assert.Equal("quick", summary.GetProperty("depth").GetString());
            var report = PreflightReports.Read(await rig.Ok($"PREFLIGHT REPORT {id}"))!;
            Assert.False(report.Complete);
            var late = report.Rows.Single(r => r.Section == "sync");
            Assert.Equal((Light.Grey, "did not finish in 3600 s"), (late.Light, late.Note));
            // What was not checked is not ready: the check that did not finish keeps its step open.
            Assert.Equal(PreflightReports.Ready([.. report.Rows.Where(r => r.Section != "sync")]).Done - 1, report.Ready.Done);
            Assert.Contains(report.Rows, r => r.Id == "outputs:op_1" && r.Light == Light.Green);
            Assert.Contains(report.Rows, r => r.Id == "media:md00000001" && r.Light == Light.Green);
            Assert.Equal(report.Rows.Count(r => r.Light == Light.Green), summary.GetProperty("green").GetInt32());
            Assert.Equal(PreflightReports.ReadyWords(report.Ready), summary.GetProperty("ready").GetString());
            Assert.True(File.Exists(Path.Combine(folder, "preflight", id + ".json")));

            var item = rig.Context().GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "preflight");
            Assert.Contains("show-ready ", item.GetProperty("state").GetString(), StringComparison.Ordinal);
            Assert.Contains(rig.Engine.Journal.Recent(20), r => r.Kind == "preflight" && r.Text.StartsWith("preflight QUICK by Desk: ", StringComparison.Ordinal));
            ShowEvent done;
            lock (events)
                done = events.Single(e => e.Kind == "preflight.done");
            Assert.Contains($"\"id\":\"{id}\"", done.FieldsJson, StringComparison.Ordinal);
            Assert.Equal("FULL preflight (decoding every clip) is not on this node — PREFLIGHT QUICK reads the facts", await rig.Refused("PREFLIGHT FULL"));

            // Another engine beside the same journal folder reads the report from its kept file.
            await using var later = new ShowRig(services: s => s with { JournalPath = Path.Combine(folder, "journal-later.jsonl") });
            var kept = PreflightReports.Read(await later.Ok($"PREFLIGHT REPORT {id}"))!;
            Assert.Equal(report with { Rows = kept.Rows }, kept);
            Assert.Equal(report.Rows, kept.Rows);
            Assert.Equal("no preflight report pf00000000", await later.Refused("PREFLIGHT REPORT pf00000000"));
            Assert.Equal("PREFLIGHT REPORT [<id>]", await later.Refused($"PREFLIGHT REPORT {id} again"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AReportRoundTripsAndShowReadyCountsTheStepsThatApply()
    {
        PreflightRow Row(string section, Light light, string? fix = null) =>
            new($"{section}:x", "FOH-A", section, "Item", light, "value", "note", fix);
        var rows = new[] { Row("outputs", Light.Green), Row("media", Light.Red, "MEDIA FETCH md00000001"), Row("devices", Light.Amber), Row("safety", Light.Grey, "SHOW ARM") };
        var ready = PreflightReports.Ready(rows);
        Assert.Equal((3, 5), (ready.Done, ready.Of));
        Assert.Equal("media: Item — note", ready.FirstOpen);
        Assert.Equal(["sync and backup"], PreflightReports.NotApplying(rows));
        // A check that did not finish leaves its step open, grey as it is; a grey row of a check that finished does not.
        var unfinished = PreflightReports.Unfinished("FOH-A", "sync", "did not finish in 5 s");
        Assert.True(PreflightReports.IsUnfinished(unfinished));
        Assert.False(PreflightReports.IsUnfinished(rows[3]));
        Assert.Equal(Light.Grey, unfinished.Light);
        var open = PreflightReports.Ready([.. rows.Where(r => r.Section is "outputs" or "safety"), unfinished]);
        Assert.Equal((4, 5, "sync and backup: sync — did not finish in 5 s"), (open.Done, open.Of, open.FirstOpen));

        var report = new PreflightReport("pf1a2b3c4d", 1_760_000_000_000, PreflightDepth.Quick, 1800, rows, ready, true);
        var json = MainState(report);
        var back = PreflightReports.Read(json)!;
        Assert.Equal(report with { Rows = back.Rows }, back);
        Assert.Equal(rows, back.Rows);
        Assert.Equal(json, MainState(back));
        Assert.Null(PreflightReports.Read("{\"id\":1}"));
    }

    private static string MainState(PreflightReport report)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
            PreflightReports.Write(w, report);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
