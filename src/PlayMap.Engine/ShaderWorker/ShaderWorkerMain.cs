using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>"PlayMap shader-worker": the process where user effects compile, so no compiler ever loads into the engine. It loads its
/// pinned natives once, says hello (its pid and each native's state), then answers one source at a time from its standard input with
/// the road's stages on its standard output, one JSON line each. Its input closing (the engine went) or a stop line ends it.</summary>
public static class ShaderWorkerMain
{
    public const string Word = "shader-worker";

    /// <summary>"--fake-compiler abort|hang" after the word (tests only): the worker answers every source as a compiler that stops
    /// its process would (abort: it ends the process mid-compile) or as one that never returns (hang: it answers nothing), so the
    /// engine's supervision is tested without a compiler that misbehaves.</summary>
    public const string FakeWord = "--fake-compiler";

    /// <summary>The exit code of a fake compiler that aborted (a native abort's).</summary>
    public const int AbortedExit = 134;

    /// <summary>The fake the arguments name, or "" for the real road.</summary>
    public static string FakeOf(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var at = Array.IndexOf(args, FakeWord);
        return at >= 0 && at + 1 < args.Length && args[at + 1] is "abort" or "hang" ? args[at + 1] : "";
    }

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fake = FakeOf(args);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var natives = fake.Length > 0 ? new RoadNatives(null, null, null, null, "", "", "", "") : RoadNatives.Load();
        var road = new ShaderRoad(natives);
        stdout.WriteLine(ShaderWorkerProtocol.Hello(Environment.ProcessId,
            fake.Length > 0 ? [new NativeState("fake", true, "a fake compiler that " + (fake == "abort" ? "aborts" : "hangs"))]
                : natives.Loaded.Select(n => new NativeState(n.Name, n.Ok, n.Words))));
        using var hung = new ManualResetEventSlim(false);
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            ShaderRequest? request;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var kind = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("t", out var t) ? t.GetString() : null;
                if (kind == "stop")
                    break;
                request = kind == "compile" ? ShaderWorkerProtocol.ParseCompile(r) : null;
            }
            catch (JsonException)
            {
                // Not a protocol line: it asks for nothing.
                continue;
            }
            if (request is null)
                continue;
            switch (fake)
            {
                case "abort":
                    return AbortedExit;
                case "hang":
                    // A compile that never returns: the engine's deadline ends this process.
                    hung.Wait();
                    continue;
            }
            var clock = Stopwatch.StartNew();
            var result = road.Run(request.Fragment, request.Vertex);
            stdout.WriteLine(ShaderWorkerProtocol.Done(request.Id, result, clock.ElapsedMilliseconds));
        }
        return 0;
    }
}
