using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>"PlayMap shader-worker": the process where user effects compile, so no compiler ever loads into the engine. It runs below
/// normal priority (a compile never takes a core from the show), loads its pinned natives once, says hello (its pid and each native's
/// state), then answers one source at a time with the road's stages on its standard output, one JSON line each. Its main thread
/// only reads its standard input and the compiles run on a thread of their own, so its input closing (the engine went) or a stop
/// line ends it even while a compile that never returns holds that thread.</summary>
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
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            // A system that refuses a lower priority leaves the worker at its own; it still compiles one source at a time.
        }
        var fake = FakeOf(args);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var natives = fake.Length > 0 ? new RoadNatives(null, null, null, null, "", "", "", "") : RoadNatives.Load();
        var road = new ShaderRoad(natives);
        var write = new Lock();
        void Say(string line)
        {
            lock (write)
                stdout.WriteLine(line);
        }
        Say(ShaderWorkerProtocol.Hello(Environment.ProcessId, fake switch
        {
            "abort" => [new NativeState("fake", true, "a fake compiler that aborts")],
            "hang" => [new NativeState("fake", true, "a fake compiler that hangs")],
            _ => natives.Loaded.Select(n => new NativeState(n.Name, n.Ok, n.Words)),
        }));

        // A request read and not yet answered: counted from its read until its reply is written.
        var inHand = 0;
        using var requests = new BlockingCollection<ShaderRequest>();
        var compiler = new Thread(() =>
        {
            using var hung = new ManualResetEventSlim(false);
            foreach (var request in requests.GetConsumingEnumerable())
            {
                switch (fake)
                {
                    case "abort":
                        Environment.Exit(AbortedExit);
                        break;
                    case "hang":
                        // A compile that never returns: the engine's deadline, or its input closing, ends this process.
                        hung.Wait();
                        break;
                }
                var clock = Stopwatch.StartNew();
                var result = road.Run(request.Fragment, request.Vertex);
                Say(ShaderWorkerProtocol.Done(request.Id, result, clock.ElapsedMilliseconds));
                Interlocked.Decrement(ref inHand);
            }
        })
        { IsBackground = true, Name = "shader compile" };
        compiler.Start();

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
            Interlocked.Increment(ref inHand);
            requests.Add(request);
        }
        // Nothing more will be asked. A compile still in hand may never return, and its answer has no reader: the process ends now.
        if (Volatile.Read(ref inHand) > 0)
            Environment.Exit(0);
        requests.CompleteAdding();
        compiler.Join();
        return 0;
    }
}
