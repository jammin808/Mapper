using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Effects;
using PlayMap.Core.Wire;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Fleet;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>The shader worker as the engine runs it (the node host in its "shader-worker" role): a good source answers validated
/// DXIL and a layout; a syntax error answers its stage and the ISF line; a fake compiler that aborts is started again after a backoff
/// that doubles (counted on a manual clock), and the third stop fails the kind with words and quarantines it; a compile past the
/// deadline is ended; and the engine's own process holds no compiler module while its worker does. No test asserts a time: the
/// clocks the host waits on are moved by hand.</summary>
public sealed class ShaderWorkerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static ShaderWorkerOptions Options(TimeProvider? time = null, params string[] arguments) =>
        new() { Worker = WorkerLaunch.Dll(NodeExe.Locate()), Arguments = [.. arguments], Time = time ?? TimeProvider.System };

    private static ShaderWorkerHost Host(ShaderWorkerOptions options, ShaderQuarantine? quarantine = null) =>
        new(options, quarantine ?? new ShaderQuarantine(), NullLogger.Instance, () => 1_760_000_000_000, "0.2.0-dev+test");

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
    public async Task AWorkerThatStopsIsStartedAgainWithBackoffAndTheThirdStopQuarantinesTheKind()
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

        // The kind is quarantined for its stops, and never sent again: the next request is answered without a worker.
        Assert.True(quarantine.IsQuarantined(invert.Hash, out var entry));
        Assert.Equal(QuarantineReason.CompilerStopped, entry.Reason);
        Assert.Equal("Invert", entry.Kind);
        Assert.Equal("0.2.0-dev+test", entry.Build);
        var again = await host.CompileAsync("Invert", invert.Fragment, invert.Vertex, CancellationToken.None);
        Assert.Equal(ShaderWorkerHost.QuarantineStage, again.Stage);
        Assert.Contains("FX RELEASE", again.Words, StringComparison.Ordinal);
        Assert.Equal(2, host.Facts.Restarts);
        Assert.All(again.Stages.Skip(1), s => Assert.Null(s.Ok));
    }

    [Fact]
    public async Task ACompilePastTheDeadlineIsEndedWithWords()
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
        Assert.Contains("ran past 10 s and was ended", reply.Words, StringComparison.Ordinal);
        Assert.Contains("1 of 3", reply.Words, StringComparison.Ordinal);
        Assert.False(host.Facts.Running);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid).Kill());
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
