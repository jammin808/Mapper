using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Model;
using PlayMap.Engine.Host;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>Optimise on import end to end, through the real worker process (tools/PlayMap.Node optimise-worker) on llvmpipe and
/// FFmpeg 9: a 2 s 1080p MPEG-4 clip with AAC made in the test, imported under MEDIA RULE MODE AUTO, becomes a verified HAP Q
/// version with its PSNR recorded as one edit; ffprobe finds the audio's start and duration equal to the source's within 1 ms;
/// playback opens the version on the GPU (STATE.decode.sources[].codec "hap_q") and MEDIA USE SOURCE leaves the open decoder alone
/// until the next open. Killing the worker mid-job restarts it and the job runs again, or fails with words when it may not; an
/// armed node starts nothing; CANCEL leaves no partial file. optimise.json and its cards are written here.</summary>
[Collection("render host")]
public sealed class OptimiseJobTests(ITestOutputHelper log)
{
    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    [FfmpegFact]
    public async Task AnImportUnderAutoBecomesAVerifiedHapQVersionThatPlaysOnTheGpuWithTheSourcesAudio()
    {
        var project = Project.CreateDefault("Optimise") with { Outputs = [Rendering.HostRig.Screen("op_1", SourceRef.None, 320, 180)] };
        await using var rig = OptimiseRig.Start(project, render: true);
        var clip = OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 1920, 1080, 2);

        var mode = await rig.Ok("MEDIA RULE MODE AUTO");
        Assert.Contains("\"mode\":\"auto\"", mode, StringComparison.Ordinal);
        Assert.False((await rig.Run("MEDIA RULE MODE AUTO")).Ok);
        var id = await rig.ImportAsync(clip, "Clip");
        await rig.Until(() => rig.Service.Jobs.Any(j => j.Media == id), "the import's job");
        var job = rig.Service.Jobs.Single(j => j.Media == id);
        Assert.Equal("hap-q", job.To);
        await rig.Until(() => JobStates.IsOver(rig.Job(job.Id).State), "the job's end");
        job = rig.Job(job.Id);
        log.WriteLine($"{job.State}: {job.Words}");
        Assert.Equal(JobStates.Done, job.State);
        Assert.Equal(("optimised Clip → hap-q", 50L), (rig.Engine.Store.UndoLabel, job.Frames));

        var item = rig.Engine.Store.Project.FindMedia(id)!;
        var v = Assert.Single(item.Versions);
        Assert.Equal((job.Version, "hap-q", job.Key, "hap_q"), (v.Id, v.To, v.Key, v.Info.VideoCodec));
        Assert.Equal((1920, 1080), (v.Info.Width, v.Info.Height));
        Assert.Equal(3, v.Quality.Sampled);
        Assert.True(v.Quality.PsnrMin >= 30 && v.Quality.PsnrAvg >= v.Quality.PsnrMin, $"{v.Quality}");
        Assert.StartsWith("hapenc/1 · gl-frag/1 · ", v.Encoder, StringComparison.Ordinal);
        var file = rig.Media.PathForHash(v.ContentHash);
        Assert.NotNull(file);
        Assert.Equal(new FileInfo(file!).Length, v.SizeBytes);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(rig.Home.Folder, "optimise", "work")));

        // The version's streams: HAP Q video, the source's AAC copied with its own timing.
        var source = OptimiseClips.Streams(clip);
        var made = OptimiseClips.Streams(file!);
        Assert.Equal(("video", "HapY"), (made[0].Type, made[0].Tag));
        var sa = source.Single(s => s.Type == "audio");
        var ma = made.Single(s => s.Type == "audio");
        log.WriteLine($"audio: source start {sa.Start} duration {sa.Duration}; version start {ma.Start} duration {ma.Duration}");
        Assert.Equal("aac", ma.Codec);
        Assert.True(Math.Abs(sa.Start - ma.Start) <= 0.001, $"audio start {sa.Start} → {ma.Start}");
        Assert.True(Math.Abs(sa.Duration - ma.Duration) <= 0.001, $"audio duration {sa.Duration} → {ma.Duration}");

        // Playback opens the version on the GPU.
        await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
        await rig.Until(() => rig.Media.OpenDecoders().Any(d => d.ItemId == id), "the decoder");
        await rig.Host!.FramesAsync(3).WaitAsync(OptimiseRig.Patience);
        var decode = rig.State().GetProperty("decode").GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("media").GetString() == id);
        Assert.Equal(("hap_q", "hap-gpu"), (decode.GetProperty("codec").GetString(), decode.GetProperty("path").GetString()));
        using (var versions = JsonDocument.Parse(await rig.Ok("MEDIA VERSIONS " + id)))
        {
            Assert.Equal(v.Id, versions.RootElement.GetProperty("playsHere").GetString());
            Assert.StartsWith("HAP Q, present, ", versions.RootElement.GetProperty("why").GetString(), StringComparison.Ordinal);
        }

        // A change of use is for the next open: the open decoder keeps playing the version.
        var open = rig.Media.OpenDecoders().Single(d => d.ItemId == id).Source;
        using (var use = JsonDocument.Parse(await rig.Ok($"MEDIA USE {id} SOURCE")))
            Assert.Contains("keeps playing what they opened", use.RootElement.GetProperty("effect").GetString(), StringComparison.Ordinal);
        Assert.False((await rig.Run($"MEDIA USE {id} SOURCE")).Ok);
        await rig.Host.FramesAsync(3).WaitAsync(OptimiseRig.Patience);
        Assert.Same(open, rig.Media.OpenDecoders().Single(d => d.ItemId == id).Source);
        await rig.Ok("OUTPUT SOURCE op_1 none");
        await rig.Until(() => rig.Media.OpenDecoders().All(d => d.ItemId != id), "the decoder's close");
        await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
        await rig.Until(() => rig.Media.OpenDecoders().Any(d => d.ItemId == id), "the source's decoder");
        Assert.Equal("mpeg4", rig.Media.OpenDecoders().Single(d => d.ItemId == id).Codec);
        Assert.False((await rig.Run($"MEDIA OPTIMISE {id} TO hap-q")).Ok);

        // The fixture: STATE.media.optimise with what moves between runs fixed.
        var optimise = rig.State().GetProperty("media").GetProperty("optimise");
        Assert.Equal(("auto", "run", 0, 0), (optimise.GetProperty("mode").GetString(), optimise.GetProperty("governor").GetString(),
            optimise.GetProperty("running").GetInt32(), optimise.GetProperty("queued").GetInt32()));
        var row = optimise.GetProperty("jobs").EnumerateArray().Single();
        Assert.Equal((job.Id, "done", 50L, v.Id), (row.GetProperty("id").GetString(), row.GetProperty("state").GetString(),
            row.GetProperty("framesDone").GetInt64(), row.GetProperty("version").GetString()));
        await Fixture(optimise);
    }

    private static async Task Fixture(JsonElement optimise)
    {
        var node = JsonNode.Parse(optimise.GetRawText())!.AsObject();
        var worker = node["worker"]!.AsObject();
        worker["pid"] = null;
        worker["gl"] = "4.5 (Core Profile) Mesa · llvmpipe";
        worker["encoder"] = "gl-frag/1 · llvmpipe";
        foreach (var j in node["jobs"]!.AsArray())
        {
            var o = j!.AsObject();
            o["id"] = "oj_fixture";
            o["media"] = "md_fixture";
            o["version"] = "mv_fixture";
            o["fps"] = 0;
            o["psnrAvg"] = 44.1;
            o["words"] = "aac copied; 44.1 dB average, 43.9 dB lowest";
        }
        var root = new JsonObject { ["media"] = new JsonObject { ["optimise"] = node } };
        var fixture = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var cards = JsonNode.Parse(Cards)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("optimise.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("optimise.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("optimise.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("optimise.cards.json"))).ReplaceLineEndings("\n"), cards);
    }

    /// <summary>What the desk's generic card template renders for STATE.media.optimise: the governor's card and a row per job.</summary>
    internal const string Cards = """
        [
          {
            "id": "media.optimise",
            "title": "Optimise",
            "path": "media.optimise",
            "light": "",
            "words": "media.optimise.words",
            "fields": [
              { "label": "Mode", "path": "media.optimise.mode" },
              { "label": "Governor", "path": "media.optimise.governor" },
              { "label": "Running", "path": "media.optimise.running" },
              { "label": "Queued", "path": "media.optimise.queued" },
              { "label": "Failed", "path": "media.optimise.failed" },
              { "label": "Worker GL", "path": "media.optimise.worker.gl" },
              { "label": "Restarts", "path": "media.optimise.worker.restarts" }
            ],
            "actions": [ "MEDIA OPTIMISE PAUSE ALL", "MEDIA OPTIMISE RESUME ALL", "MEDIA OPTIMISE CANCEL ALL", "MEDIA RULE MODE AUTO", "MEDIA RULE MODE NEVER" ]
          },
          {
            "id": "media.optimise.job",
            "title": "Optimise job",
            "path": "media.optimise.jobs[]",
            "key": "id",
            "light": "",
            "words": "words",
            "fields": [
              { "label": "Clip", "path": "name" },
              { "label": "To", "path": "to" },
              { "label": "State", "path": "state" },
              { "label": "Frames", "path": "framesDone" },
              { "label": "Of", "path": "frames" },
              { "label": "ETA s", "path": "etaS" },
              { "label": "PSNR dB", "path": "psnrAvg" }
            ],
            "actions": [ "MEDIA OPTIMISE PAUSE {id}", "MEDIA OPTIMISE RESUME {id}", "MEDIA OPTIMISE CANCEL {id}", "MEDIA VERSIONS {media}" ]
          }
        ]
        """;

    [FfmpegFact]
    public async Task AWorkerKilledMidJobIsRestartedAndTheJobRunsAgainToItsVersion()
    {
        await using var rig = OptimiseRig.Start();
        var clip = OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 640, 360, 10);
        var id = await rig.ImportAsync(clip, "Clip");
        using var reply = JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE {id} TO hap"));
        Assert.Equal("accepted", reply.RootElement.GetProperty("stage").GetString());
        var jobId = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await rig.Until(() => rig.Job(jobId).FramesDone >= 1, "the first frames");
        // Held between frames, so the kill lands mid-job however fast the encoder runs.
        await rig.Ok($"MEDIA OPTIMISE PAUSE {jobId}");
        var pid = rig.Service.View.Pid!.Value;
        using (var worker = Process.GetProcessById(pid))
            worker.Kill();
        await rig.Until(() => rig.Job(jobId) is { State: JobStates.Queued, Attempts: 1 }, "the job back in the queue");
        Assert.StartsWith("the worker stopped (exit code ", rig.Job(jobId).Words, StringComparison.Ordinal);
        Assert.Equal(1, rig.Service.View.Restarts);
        Assert.False(File.Exists(Path.Combine(rig.Home.Folder, "optimise", "work", jobId + ".mov.part")));
        // The restart waits out its backoff on the node's clock, and the job its release.
        await rig.Ok($"MEDIA OPTIMISE RESUME {jobId}");
        rig.Clock.Advance(2_000_000_000);
        await rig.Until(() => JobStates.IsOver(rig.Job(jobId).State), "the job's end");
        Assert.Equal(JobStates.Done, rig.Job(jobId).State);
        Assert.NotEqual(pid, rig.Service.View.Pid);
        Assert.Single(rig.Engine.Store.Project.FindMedia(id)!.Versions);
    }

    [FfmpegFact]
    public async Task AJobThatMayNotRunAgainFailsWithWordsWhenItsWorkerIsKilled()
    {
        await using var rig = OptimiseRig.Start(Project.CreateDefault("Optimise") with { Outputs = [Rendering.HostRig.Screen("op_1", SourceRef.None, 320, 180)] },
            options: new OptimiseOptions { Worker = WorkerLaunch.Dll(NodeExe.Locate()), Step = TimeSpan.FromMilliseconds(100), MaxAttempts = 1 });
        var id = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 640, 360, 10), "Clip");
        using var reply = JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE {id} TO hap"));
        var jobId = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await rig.Until(() => rig.Job(jobId).FramesDone >= 1, "the first frames");
        await rig.Ok($"MEDIA OPTIMISE PAUSE {jobId}");
        using (var worker = Process.GetProcessById(rig.Service.View.Pid!.Value))
            worker.Kill();
        await rig.Until(() => rig.Job(jobId).State == JobStates.Failed, "the job's failure");
        // A failed job of an item the show plays turns the context's optimise item red with its words.
        await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
        var item = ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "optimise");
        Assert.Equal(Core.Bus.Light.Red, item.Light);
        Assert.StartsWith("Clip: the worker stopped (exit code ", item.State, StringComparison.Ordinal);
        Assert.Matches("^the worker stopped \\(exit code -?\\d+\\) once on this clip — it is not optimised$", rig.Job(jobId).Words);
        Assert.Empty(rig.Engine.Store.Project.FindMedia(id)!.Versions);
    }

    [FfmpegFact]
    public async Task UnderNeverAnImportQueuesNothingAndUnderAutoTheNextOneIsQueued()
    {
        await using var rig = OptimiseRig.Start();
        await rig.Ok("MEDIA RULE MODE NEVER");
        var never = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 1920, 1080, 0.2, "never.mp4"), "Never");
        await rig.Ok("MEDIA RULE MODE AUTO");
        var auto = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 1920, 1080, 0.2, "auto.mp4"), "Auto");
        await rig.Until(() => rig.Service.Jobs.Any(j => j.Media == auto), "the auto import's job");
        // The hook plans imports in the order their probes land: the first one was planned under never.
        Assert.DoesNotContain(rig.Service.Jobs, j => j.Media == never);
        await rig.Until(() => rig.Service.Jobs.All(j => JobStates.IsOver(j.State)), "the job's end");
        Assert.Contains("queued", string.Join(" | ", rig.Engine.Journal.Recent(50).Select(r => r.Text)), StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task ANodeStoppedMidJobQueuesTheJobAgainWhenItStarts()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-optimise-");
        string jobId;
        var first = OptimiseRig.Start(dir: dir);
        var id = await first.ImportAsync(OptimiseClips.Mpeg4WithAudio(dir.FullName, 640, 360, 10), "Clip");
        using (var reply = JsonDocument.Parse(await first.Ok($"MEDIA OPTIMISE {id} TO hap")))
            jobId = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await first.Until(() => first.Job(jobId).FramesDone >= 1, "the first frames");
        await first.Ok($"MEDIA OPTIMISE PAUSE {jobId}");
        var project = first.Engine.Store.Project;
        await first.StopAsync();
        // A clean stop is not the job's end: on disk it is still running, and the next start queues it again.
        await using var second = OptimiseRig.Start(project, dir: dir);
        var again = second.Job(jobId);
        Assert.Equal((JobStates.Queued, 0, true), (again.State, again.Attempts, again.Paused));
        Assert.Equal("the node stopped while it ran: it starts again from its first frame", again.Words);
        Assert.False(File.Exists(Path.Combine(second.Home.Folder, "optimise", "work", jobId + ".mov.part")));
        await second.Ok($"MEDIA OPTIMISE RESUME {jobId}");
        await second.Until(() => JobStates.IsOver(second.Job(jobId).State), "the job's end");
        Assert.Equal(JobStates.Done, second.Job(jobId).State);
    }

    [FfmpegFact]
    public async Task AVersionBelowTheNodesFloorIsNotKept()
    {
        await using var rig = OptimiseRig.Start(settings: s => s with { MinPsnr = 99 });
        var id = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 320, 180, 1), "Clip");
        using var reply = JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE {id} TO hap"));
        var jobId = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await rig.Until(() => JobStates.IsOver(rig.Job(jobId).State), "the job's end");
        Assert.Equal(JobStates.Failed, rig.Job(jobId).State);
        Assert.Matches("^lowest \\d+\\.\\d dB at \\d+\\.\\d s is below this node's floor of 99 dB — the version is not kept$", rig.Job(jobId).Words);
        Assert.Empty(rig.Engine.Store.Project.FindMedia(id)!.Versions);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(rig.Home.Folder, "optimise", "work")));
        Assert.Empty(Directory.EnumerateFiles(rig.Home.MediaStore, "*.mov"));
    }

    [FfmpegFact]
    public async Task LedFramesDroppedOrLateWhileAJobRunsPauseTheGovernor()
    {
        // An LED-only rig: no output window, no playlist — the LED sender is the air.
        var led = new FakeLed();
        await using var rig = OptimiseRig.Start(led: led);
        var id = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 640, 360, 10), "Clip");
        using var reply = JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE {id} TO hap"));
        var jobId = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await rig.Until(() => rig.Job(jobId).FramesDone >= 1, "the first frames");
        // Held between frames: the job still runs, so what the air loses now is the job's.
        await rig.Ok($"MEDIA OPTIMISE PAUSE {jobId}");
        string Governor() => rig.State().GetProperty("media").GetProperty("optimise").GetProperty("governor").GetString()!;
        Assert.Equal("run", Governor());
        led.Dropped = 3;
        await rig.Until(() => Governor() == "paused", "the pause for dropped LED frames");
        Assert.Equal("paused for 60 s: 3 frames dropped while it ran",
            rig.State().GetProperty("media").GetProperty("optimise").GetProperty("words").GetString());
        // The window passes on the node's clock; then a node whose frames leave late pauses it again.
        rig.Clock.Advance(61_000_000_000);
        await rig.Until(() => Governor() == "run", "the end of the pause");
        led.Late = true;
        await rig.Until(() => Governor() == "paused", "the pause for a late LED node");
        Assert.EndsWith("late while it ran", rig.State().GetProperty("media").GetProperty("optimise").GetProperty("words").GetString(), StringComparison.Ordinal);
        await rig.Ok($"MEDIA OPTIMISE CANCEL {jobId}");
    }

    /// <summary>An LED sender as the engine reads it: a node that sends (or sends late) and the frames dropped so far.</summary>
    private sealed class FakeLed : Core.Led.ILedOutput
    {
        public volatile int Dropped;
        public volatile bool Late;

        public Core.Led.ILedFrameSink Sink { get; } = new Core.Led.LedFramePool();

        public void Apply(Core.Led.LedSendProgram program)
        {
            // Nothing is sent: the view is the test's.
        }

        public void SetMaster(bool on, bool blackFirst)
        {
            // As Apply.
        }

        public void StopNode(string nodeId, bool blackFirst)
        {
            // As Apply.
        }

        public Core.Led.LedOutputView View() => new(true,
            [new Core.Led.LedNodeView("ln_1", Late ? "late" : "sending", 60, 1, Late ? 40 : 2, 0, null, Late ? "frames leave 40.0 ms after their instant" : "", 600, 600)],
            Dropped, 0);

        public bool TryMonitor(string nodeId, int universe, Span<byte> slots, out long leaderNs)
        {
            leaderNs = 0;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [FfmpegFact]
    public async Task AnArmedNodeStartsNothingAndRefusesNewJobs()
    {
        await using var rig = OptimiseRig.Start();
        var id = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 320, 180, 1), "Clip");
        rig.Gate.IsArmed = true;
        // The router's gate refuses an armed line before the verb; the verb's own words say the same of the service.
        Assert.False((await rig.Run($"MEDIA OPTIMISE {id} TO hap")).Ok);
        rig.Gate.IsArmed = false;
        // Queued while in rehearsal, then the show is armed before the next step: the governor waits and no worker starts.
        rig.Gate.IsArmed = true;
        var queued = rig.Service.Request([rig.Engine.Store.Project.FindMedia(id)!], "hap", EncodeQuality.Fast);
        var jobId = Assert.Single(queued).Job!.Id;
        await rig.Until(() => rig.State().GetProperty("media").GetProperty("optimise").GetProperty("governor").GetString() == "waiting", "the governor's wait");
        var optimise = rig.State().GetProperty("media").GetProperty("optimise");
        Assert.Equal("optimise waits: the show is armed", optimise.GetProperty("words").GetString());
        for (var step = 0; step < 5; step++)
            rig.Service.StepOnce();
        Assert.Equal(JobStates.Queued, rig.Job(jobId).State);
        Assert.Null(rig.Service.View.Pid);
        rig.Gate.IsArmed = false;
        await rig.Until(() => JobStates.IsOver(rig.Job(jobId).State), "the job's end");
        Assert.Equal(JobStates.Done, rig.Job(jobId).State);
    }

    [FfmpegFact]
    public async Task CancelStopsTheRunningJobAndLeavesNoPartialFile()
    {
        await using var rig = OptimiseRig.Start();
        var id = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 640, 360, 10), "Clip");
        using var reply = JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE {id} TO hap-q"));
        var jobId = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        var partial = Path.Combine(rig.Home.Folder, "optimise", "work", jobId + ".mov.part");
        await rig.Until(() => rig.Job(jobId).FramesDone >= 1 && File.Exists(partial), "the partial file");
        // A second job waits behind it: the cancel must not hand it to the worker being killed.
        var second = Assert.Single(rig.Service.Request([rig.Engine.Store.Project.FindMedia(id)!], "hap", EncodeQuality.Fast)).Job!.Id;
        using (var cancelled = JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE CANCEL {jobId}")))
            Assert.Equal(jobId, cancelled.RootElement.GetProperty("cancelled")[0].GetString());
        Assert.Equal(JobStates.Cancelled, rig.Job(jobId).State);
        await rig.Until(() => !File.Exists(partial), "the partial file's removal");
        Assert.False((await rig.Run($"MEDIA OPTIMISE CANCEL {jobId}")).Ok);
        await rig.Until(() => JobStates.IsOver(rig.Job(second).State), "the second job's end");
        Assert.Equal(JobStates.Done, rig.Job(second).State);
        Assert.False(File.Exists(partial));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(partial)!));
        Assert.Equal("hap", Assert.Single(rig.Engine.Store.Project.FindMedia(id)!.Versions).To);
        // The queue on disk says the same.
        Assert.Contains("\"state\": \"cancelled\"", await File.ReadAllTextAsync(Path.Combine(rig.Home.Folder, "optimise", "jobs.json")), StringComparison.Ordinal);
    }
}
