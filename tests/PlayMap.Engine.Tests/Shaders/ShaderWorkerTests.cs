using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Fleet;
using PlayMap.Engine.Tests.Show;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Render.Effects;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>The shader worker as the engine runs it (the node host in its "shader-worker" role): a good source answers validated
/// DXIL and a layout; a syntax error answers its stage and the ISF line; a fake compiler that aborts is started again after a backoff
/// that doubles (counted on a manual clock), and the third stop fails the source with words in the host's own memory, writing no
/// quarantine; a compile past the deadline is ended and counts no stop; a compile its caller abandons ends its worker, so the next
/// source meets a fresh one; a node that stops mid-compile answers so and fails nothing; the worker's own process ends when its input
/// closes, mid-compile too, and runs below normal priority; FX CHECK on an
/// armed node reads the host's own words when its bound ends a check, and three such ends change no quarantine, so a GL runner given
/// the node's quarantine still draws the kind; and the engine's own process holds no compiler module while its worker does. No test
/// asserts a time: the clocks the host waits on are moved by hand.</summary>
public sealed class ShaderWorkerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static ShaderWorkerOptions Options(TimeProvider? time = null, params string[] arguments) =>
        new() { Worker = WorkerLaunch.Dll(NodeExe.Locate()), Arguments = [.. arguments], Time = time ?? TimeProvider.System };

    private static ShaderWorkerHost Host(ShaderWorkerOptions options, ShaderQuarantine? quarantine = null) =>
        new(options, quarantine ?? new ShaderQuarantine(), NullLogger.Instance);

    private static FxSource Builtin(string id) => FxCatalog.Builtin.Source(id)!;

    /// <summary>Waits (in real time, bounded) until <paramref name="ready"/> holds.</summary>
    private static async Task Until(Func<bool> ready, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(waited.Elapsed < Patience, "never: " + what);
            await Task.Delay(5);
        }
    }

    private static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task AGoodSourceAnswersValidatedDxilAndItsLayout()
    {
        await using var host = Host(Options());
        var levels = Builtin("fx.levels");
        var reply = await host.CompileAsync("Levels", levels.Fragment, levels.Vertex, CancellationToken.None);
        Assert.True(reply.Ok, $"{reply.Stage}: {reply.Words}");
        Assert.Equal(ShaderRoad.StageNames, reply.Stages.Select(s => s.Name));
        Assert.All(reply.Stages, s => Assert.True(s.Ok));
        Assert.Equal(2, reply.Dxil.Length);
        Assert.All(reply.Dxil, d => Assert.True(new DxilOutput(true, d, [], [], "").Signed));
        Assert.Equal(2, reply.Reflection.Length);
        Assert.Equal(["inBlack", "inWhite", "gamma", "outBlack", "outWhite"], reply.Layout.Constants.Take(5).Select(c => c.Name));
        Assert.Equal(new LayoutTexture("inputImage", 0, true), Assert.Single(reply.Layout.Textures));

        var facts = host.Facts;
        Assert.True(facts.Running);
        Assert.NotNull(facts.Pid);
        Assert.Equal((0, 1, 0), (facts.Restarts, facts.Compiled, facts.Failed));
        Assert.Equal(["glslang", "spirv-val", "spirv-cross", "dxc"], facts.Natives.Select(n => n.Name));
        Assert.All(facts.Natives, n => Assert.True(n.Ok, n.Words));
    }

    [Fact]
    public async Task ASyntaxErrorAnswersItsStageAndTheIsfLine()
    {
        await using var host = Host(Options());
        const string fs = "/*{ \"INPUTS\": [ ] }*/\nvoid main() {\n    vec4 c = vec4(1.0);\n    gl_FragColor = c * notDeclared;\n}\n";
        var reply = await host.CompileAsync("Broken", fs, null, CancellationToken.None);
        Assert.False(reply.Ok);
        Assert.Equal(ShaderRoad.GlslangStage, reply.Stage);
        Assert.Equal(4, reply.IsfLine);
        Assert.Contains("notDeclared", reply.Words, StringComparison.Ordinal);
        Assert.Equal([true, false, null, null, null], reply.Stages.Select(s => s.Ok));
        Assert.Equal(4, reply.Stages[1].Line);
        Assert.Empty(reply.Dxil);
        Assert.Equal((1, 0), (host.Facts.Failed, host.Facts.Compiled));
        // A worker that answered a failure is the same worker: nothing stopped it.
        Assert.Equal(0, host.Facts.Restarts);
    }

    [Fact]
    public async Task AWorkerThatStopsIsStartedAgainWithBackoffAndTheThirdStopFailsTheSourceWithoutAQuarantine()
    {
        var time = new ManualTime();
        var quarantine = new ShaderQuarantine();
        await using var host = Host(Options(time, ShaderWorkerMain.FakeWord, "abort"), quarantine);
        var invert = Builtin("fx.invert");
        var compile = host.CompileAsync("Invert", invert.Fragment, invert.Vertex, CancellationToken.None);

        // The first stop: a backoff of 1 s, then a second worker; the second stop: 2 s, then a third. A tick short of each, the
        // worker is not started again.
        foreach (var backoff in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) })
        {
            await Until(() => host.BackingOff && time.Waiting > 0, "a backoff");
            var started = host.Facts.Restarts;
            time.Advance(backoff - TimeSpan.FromTicks(1));
            Assert.True(host.BackingOff);
            Assert.Equal(started, host.Facts.Restarts);
            time.Advance(TimeSpan.FromTicks(1));
            await Until(() => host.Facts.Restarts == started + 1 || compile.IsCompleted, "a restart");
        }
        var reply = await compile.WaitAsync(Patience);
        Assert.False(reply.Ok);
        Assert.Equal(ShaderWorkerHost.WorkerStage, reply.Stage);
        Assert.Contains("stopped the shader worker 3 times", reply.Words, StringComparison.Ordinal);
        Assert.Contains($"exit {ShaderWorkerMain.AbortedExit}", reply.Words, StringComparison.Ordinal);
        Assert.Equal(2, host.Facts.Restarts);

        // The source fails in the host's memory, not the node's quarantine: nothing the air reads changed. It is never sent again:
        // the next request is answered at once with the same words, and no worker is started for it.
        Assert.Empty(quarantine.All);
        var again = await host.CompileAsync("Invert", invert.Fragment, invert.Vertex, CancellationToken.None);
        Assert.Equal(ShaderWorkerHost.WorkerStage, again.Stage);
        Assert.Equal(reply.Words, again.Words);
        Assert.Equal(2, host.Facts.Restarts);
        Assert.False(host.Facts.Running);
        Assert.All(again.Stages.Skip(1), s => Assert.Null(s.Ok));
        Assert.Empty(quarantine.All);
    }

    [Fact]
    public async Task ACompilePastTheDeadlineIsEndedWithWordsAndCountsNoStop()
    {
        var time = new ManualTime();
        await using var host = Host(Options(time, ShaderWorkerMain.FakeWord, "hang"));
        var blur = Builtin("fx.blur");
        var compile = host.CompileAsync("Blur", blur.Fragment, blur.Vertex, CancellationToken.None);
        await Until(() => host.Facts.Running && time.Waiting > 0, "the compile's deadline");
        var pid = host.Facts.Pid!.Value;
        time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False(compile.IsCompleted);
        time.Advance(TimeSpan.FromTicks(1));
        var reply = await compile.WaitAsync(Patience);
        Assert.Equal(ShaderWorkerHost.WorkerStage, reply.Stage);
        Assert.Contains("the compile ran past 10 s and was ended", reply.Words, StringComparison.Ordinal);
        Assert.DoesNotContain(" of 3", reply.Words, StringComparison.Ordinal);
        Assert.Equal(0, host.StopsOf(blur.Hash));
        Assert.False(host.Facts.Running);
        Assert.True(Gone(pid));
    }

    [Fact]
    public async Task ACompileItsCallerAbandonsEndsItsWorkerAndTheNextSourceMeetsAFreshOne()
    {
        var time = new ManualTime();
        await using var host = Host(Options(time, ShaderWorkerMain.FakeWord, "hang"));
        var blur = Builtin("fx.blur");
        var levels = Builtin("fx.levels");
        using var caller = new CancellationTokenSource();
        var abandoned = host.CompileAsync("Blur", blur.Fragment, blur.Vertex, caller.Token);
        await Until(() => host.Facts.Running && time.Waiting > 0, "the first compile in hand");
        var first = host.Facts.Pid!.Value;

        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Patience));
        // The worker that held the abandoned source is gone before the lane is let go, and nothing is counted against anyone.
        Assert.True(Gone(first));
        Assert.Equal(0, host.StopsOf(blur.Hash));

        var next = host.CompileAsync("Levels", levels.Fragment, levels.Vertex, CancellationToken.None);
        await Until(() => host.Facts is { Running: true, Pid: { } pid } && pid != first && time.Waiting > 0, "a fresh worker");
        Assert.Equal(1, host.Facts.Restarts);
        time.Advance(TimeSpan.FromSeconds(10));
        var reply = await next.WaitAsync(Patience);
        Assert.Contains("the compile ran past 10 s and was ended", reply.Words, StringComparison.Ordinal);
        Assert.Equal(0, host.StopsOf(levels.Hash));
        Assert.Equal(0, host.StopsOf(blur.Hash));
    }

    [Fact]
    public async Task ANodeThatStopsMidCompileAnswersSoAndFailsNothing()
    {
        var time = new ManualTime();
        var quarantine = new ShaderQuarantine();
        // One stop would fail the source: the stop of the node is not one.
        await using var host = Host(Options(time, ShaderWorkerMain.FakeWord, "hang") with { StopsToFail = 1 }, quarantine);
        var blur = Builtin("fx.blur");
        var compile = host.CompileAsync("Blur", blur.Fragment, blur.Vertex, CancellationToken.None);
        await Until(() => host.Facts.Running && time.Waiting > 0, "the compile in hand");
        var pid = host.Facts.Pid!.Value;

        await host.DisposeAsync();
        var reply = await compile.WaitAsync(Patience);
        Assert.Equal(ShaderWorkerHost.WorkerStage, reply.Stage);
        Assert.Contains("the node is stopping", reply.Words, StringComparison.Ordinal);
        Assert.DoesNotContain("stopped the shader worker", reply.Words, StringComparison.Ordinal);
        Assert.Equal(0, host.StopsOf(blur.Hash));
        Assert.Empty(quarantine.All);
        Assert.True(Gone(pid));
        // A source after the stop is answered so too, with no worker started and nothing thrown.
        var after = await host.CompileAsync("Blur", blur.Fragment, blur.Vertex, CancellationToken.None);
        Assert.Contains("the node is stopping", after.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChecksTheirBoundEndsOnAnArmedNodeReadTheHostsWordsAndQuarantineNothing()
    {
        var time = new ManualTime();
        var quarantine = new ShaderQuarantine();
        var options = Options(time, ShaderWorkerMain.FakeWord, "hang");
        await using var host = Host(options, quarantine);
        await using var rig = new ShowRig(services: s => ShaderParts.Compose(s, quarantine, host));
        ShaderParts.Attach(rig.Engine);
        await rig.Ok("SHOW ARM");
        var blur = Builtin("fx.blur");
        var seconds = options.CheckBound.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        // FX CHECK is a query the armed gate lets through. Each check is ended by the host's own bound, inside the engine's 10 s:
        // the report's worker stage carries the host's words, and its worker is ended.
        for (var check = 1; check <= 3; check++)
        {
            var started = host.Facts.Restarts;
            var running = rig.Run("FX CHECK fx.blur");
            await Until(() => host.Facts.Running && time.Waiting >= 2, $"check {check}'s compile in hand");
            time.Advance(options.CheckBound);
            var reply = await running.WaitAsync(Patience);
            Assert.True(reply.Ok, reply.Payload);
            using var report = JsonDocument.Parse(reply.Payload!);
            var worker = Assert.Single(report.RootElement.GetProperty("stages").EnumerateArray(),
                st => st.GetProperty("name").GetString() == ShaderWorkerHost.WorkerStage);
            Assert.False(worker.GetProperty("ok").GetBoolean());
            Assert.Contains($"the check ran past {seconds} s and was ended", worker.GetProperty("words").GetString(), StringComparison.Ordinal);
            Assert.False(host.Facts.Running);
            Assert.True(check == 1 || host.Facts.Restarts == started + 1);
        }

        // Nothing was counted or quarantined: STATE's list is as it was, and a GL runner given the node's quarantine draws the kind.
        Assert.Equal(0, host.StopsOf(blur.Hash));
        Assert.Empty(quarantine.All);
        Assert.Empty(rig.State().GetProperty("fx").GetProperty("quarantine").EnumerateArray());
        Assert.Equal(FxSlotState.Ok, DrawnThrough(quarantine, "fx.blur"));
    }

    /// <summary>A slot of <paramref name="kind"/> drawn on GL by a runner given <paramref name="quarantine"/>, frame after frame on a
    /// thread that holds its own context, until it is no longer compiling: its observed state.</summary>
    private static FxSlotState DrawnThrough(ShaderQuarantine quarantine, string kind)
    {
        var state = FxSlotState.Compiling;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var gl = new Thread(() =>
        {
            try
            {
                var options = new GlDeviceOptions(CheckErrors: true, DebugOutput: false);
                using var context = OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                    ? RenderContext.CreateSurfaceless(options) : RenderContext.CreateHidden(options);
                using var lane = new FxCompileLane(context, sharedContext: false);
                using var runner = new FxChainRunner(context.Device, lane) { Quarantine = quarantine };
                using var picture = context.Device.CreateTexture(16, 9, TextureFormat.Rgba8);
                var chain = new FxChain([new FxSlot("fx00000001", kind, "", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0)]);
                var show = Project.CreateDefault("Drawn");
                var waited = Stopwatch.StartNew();
                for (long frame = 1; state == FxSlotState.Compiling && waited.Elapsed < Patience; frame++)
                {
                    lane.Collect(frame);
                    runner.BeginFrame(frame, show);
                    runner.Apply(chain, "op_1", picture, new FxFrame(frame, 1_000_000_000, 60, 1_760_000_000_000, 0));
                    runner.EndFrame();
                    state = runner.Stats.TryGet("fx00000001", out var live) ? live.State : FxSlotState.Compiling;
                }
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        })
        { IsBackground = true, Name = "gl quarantine" };
        gl.Start();
        gl.Join();
        failure?.Throw();
        return state;
    }

    [Fact]
    public async Task TheWorkersOwnProcessRunsBelowNormalAndEndsWhenItsInputClosesMidCompile()
    {
        var launch = WorkerLaunch.Dll(NodeExe.Locate());
        var info = new ProcessStartInfo(launch.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in launch.Prefix)
            info.ArgumentList.Add(a);
        foreach (var a in new[] { ShaderWorkerMain.Word, ShaderWorkerMain.FakeWord, "hang" })
            info.ArgumentList.Add(a);
        using var worker = Process.Start(info)!;
        try
        {
            var hello = await worker.StandardOutput.ReadLineAsync().WaitAsync(Patience);
            Assert.IsType<ShaderHello>(ShaderWorkerProtocol.Parse(hello!));
            Assert.Equal(ProcessPriorityClass.BelowNormal, worker.PriorityClass);
            var blur = Builtin("fx.blur");
            await worker.StandardInput.WriteLineAsync(ShaderWorkerProtocol.Compile(new ShaderRequest(1, blur.Hash, "Blur", blur.Fragment, blur.Vertex)));
            await worker.StandardInput.FlushAsync();

            // Only the engine's side of its input is closed: the compile that never returns does not keep the process.
            worker.StandardInput.Close();
            await worker.WaitForExitAsync().WaitAsync(Patience);
            Assert.True(worker.HasExited);
        }
        finally
        {
            if (!worker.HasExited)
                worker.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task TheEnginesProcessHoldsNoCompilerModuleWhileItsWorkerDoes()
    {
        await using var host = Host(Options());
        await using var engine = new EngineCore(ShaderParts.Compose(EngineServices.ForTests(), new ShaderQuarantine(), host));
        ShaderParts.Attach(engine);
        Assert.True(WireGrammar.TryParse("FX CHECK fx.levels", out var line, out var error), error?.Reason);
        var reply = await engine.HandleAsync(line, SessionInfo.Desk("t"), CancellationToken.None);
        Assert.True(reply.Ok, reply.Payload);
        using (var report = JsonDocument.Parse(reply.Payload!))
        {
            var stages = report.RootElement.GetProperty("stages").EnumerateArray().ToList();
            Assert.Equal(ShaderRoad.StageNames, stages.Select(s => s.GetProperty("name").GetString()));
            Assert.All(stages, s => Assert.True(s.GetProperty("ok").GetBoolean(), s.GetProperty("words").GetString()));
        }
        var worker = JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("fx").GetProperty("compile").GetProperty("worker");
        Assert.True(worker.GetProperty("running").GetBoolean());
        var pid = worker.GetProperty("pid").GetInt32();
        Assert.Equal(1, worker.GetProperty("compiled").GetInt32());

        static List<string> Modules(Process p) =>
            [.. p.Modules.Cast<ProcessModule>().Select(m => Path.GetFileName(m.FileName ?? "")).Where(n => CompilerNatives.AllFiles.Contains(n))];
        using var self = Process.GetCurrentProcess();
        Assert.Empty(Modules(self));
        using var child = Process.GetProcessById(pid);
        var held = Modules(child);
        Assert.Contains(held, n => n is "libdxcompiler.so" or "dxcompiler.dll");
        Assert.Contains(held, n => n is "libshaderc_shared.so" or "shaderc_shared.dll");
        Assert.Contains(held, n => n is "libspirv-cross.so" or "spirv-cross.dll");
    }
}
