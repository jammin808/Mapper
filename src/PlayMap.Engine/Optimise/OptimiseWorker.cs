using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Optimise;
using PlayMap.Render.Encode;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Optimise;

/// <summary>"PlayMap optimise-worker": the process that encodes. It lowers its own CPU priority, makes its own GL context
/// through the platform seam (hidden, or surfaceless on Linux with no display) on its GL thread, says hello, then runs one job
/// at a time from its standard input — decode, encode, pack, mux, read back — writing progress, done or failed on its standard
/// output, one JSON line each. Knobs pace it between frames: paused and waiting hold the job, and the worker says when it holds and
/// when it goes on (its held lines: a pause is applied only once it holds); a GPU duty below one rests after
/// each frame for the time that keeps the frame's share of the clock at the duty. Its input closing (the engine went) or a stop
/// line ends it; a cancel line or a kill stops the running job, whose partial file the engine deletes.</summary>
public static class OptimiseWorker
{
    public const string Word = "optimise-worker";

    /// <summary>"--priority &lt;class&gt;" after the word: the CPU priority the worker takes (below normal when absent).</summary>
    public const string PriorityWord = "--priority";

    /// <summary>"--hold-at &lt;frames&gt;" after the word (tests only): after that many frames of its first job the worker waits for the
    /// engine's next knobs line that holds (a pause, the governor's wait) before its next frame, as a frame that takes that long would —
    /// so a pause sent then reaches it mid-job however fast the encoder runs.</summary>
    public const string HoldAtWord = "--hold-at";

    /// <summary>The frame the arguments' <see cref="HoldAtWord"/> names, 0 when none.</summary>
    public static long HoldAtOf(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var at = Array.IndexOf(args, HoldAtWord);
        return at >= 0 && at + 1 < args.Length && long.TryParse(args[at + 1], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>The priority the worker's arguments name, below normal when they name none (or one it does not know).</summary>
    public static ProcessPriorityClass PriorityOf(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var at = Array.IndexOf(args, PriorityWord);
        return at >= 0 && at + 1 < args.Length && Enum.TryParse<ProcessPriorityClass>(args[at + 1], ignoreCase: true, out var p) && Enum.IsDefined(p)
            ? p : ProcessPriorityClass.BelowNormal;
    }

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = PriorityOf(args);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            // A system that refuses a lower priority leaves the worker at its own; the governor still paces it.
        }
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var loop = new Loop(stdout, HoldAtOf(args));
        loop.Start();
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (!loop.Handle(line))
                break;
        }
        loop.Finish();
        return 0;
    }

    private sealed class Loop(StreamWriter stdout, long holdAt) : IDisposable
    {
        private readonly ManualResetEventSlim _knobsHeard = new(false);
        private long _holdAt = holdAt;
        private long _frames;
        private readonly Lock _write = new();
        private readonly BlockingCollection<WorkerJob> _jobs = [];
        private readonly ManualResetEventSlim _go = new(true);
        private readonly CancellationTokenSource _stop = new();
        private Thread? _thread;
        private volatile WorkerKnobs _knobs = new("run", 1, Math.Max(1, Environment.ProcessorCount - 1), false);
        private Action? _cancelCurrent;
        private string _currentJob = "";

        public void Start()
        {
            _thread = new Thread(GlThread) { IsBackground = true, Name = "optimise gl" };
            _thread.Start();
        }

        /// <summary>One line from the engine; false when the worker should end.</summary>
        public bool Handle(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                switch (r.TryGetProperty("t", out var t) ? t.GetString() : "")
                {
                    case "run" when WorkerProtocol.ParseRun(r) is { } job:
                        _jobs.Add(job, CancellationToken.None);
                        break;
                    case "knobs" when WorkerProtocol.ParseKnobs(r) is { } k:
                        _knobs = k;
                        if (k.Holds)
                            _go.Reset();
                        else
                            _go.Set();
                        if (k.Holds)
                            _knobsHeard.Set();
                        break;
                    case "cancel":
                        lock (_write)
                        {
                            if (r.TryGetProperty("job", out var j) && j.GetString() == _currentJob)
                                _cancelCurrent?.Invoke();
                        }
                        break;
                    case "stop":
                        return false;
                }
            }
            catch (JsonException)
            {
                // A line that is not JSON changes nothing (fail closed).
            }
            return true;
        }

        public void Finish()
        {
            _jobs.CompleteAdding();
            _stop.Cancel();
            _go.Set();
            _thread?.Join();
        }

        private void Say(string line)
        {
            lock (_write)
                stdout.WriteLine(line);
        }

        private void GlThread()
        {
            IGpuPlatform? platform = null;
            GlHapEncoder? encoder = null;
            var why = "";
            try
            {
                var surfaceless = OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
                platform = GpuPlatforms.Create(new PlatformRequest(Surfaceless: surfaceless), new GlDeviceOptions(CheckErrors: true, DebugOutput: false));
                encoder = new GlHapEncoder(platform.Context);
                var d = platform.Context.Device;
                Say(WorkerProtocol.Hello(Environment.ProcessId, $"{d.Version} · {d.Renderer}", encoder.Id));
            }
            catch (Exception ex) when (ex is GlException or PlatformNotSupportedException or InvalidOperationException or DllNotFoundException
                                           or Silk.NET.GLFW.GlfwException)
            {
                why = "no GL context for the encoder: " + ex.Message;
                Say(WorkerProtocol.Hello(Environment.ProcessId, "", ""));
            }
            try
            {
                foreach (var job in _jobs.GetConsumingEnumerable(CancellationToken.None))
                {
                    if (encoder is null)
                    {
                        Say(WorkerProtocol.Failed(job.Job, why));
                        continue;
                    }
                    RunJob(job, encoder);
                }
            }
            finally
            {
                encoder?.Dispose();
                platform?.Dispose();
            }
        }

        private void RunJob(WorkerJob job, GlHapEncoder encoder)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            lock (_write)
            {
                _cancelCurrent = cts.Cancel;
                _currentJob = job.Job;
            }
            try
            {
                if (!OptimiseTargets.TryCodec(job.To, out var codec) || !Recipe.TryQuality(job.Quality, out var quality)
                    || !Enum.TryParse<SecondStagePolicy>(job.SecondStage, true, out var stage))
                {
                    Say(WorkerProtocol.Failed(job.Job, $"the job names a recipe this worker does not make ({job.To}, {job.Quality}, {job.SecondStage})"));
                    return;
                }
                if (!encoder.Supports(codec, quality, out var why))
                {
                    Say(WorkerProtocol.Failed(job.Job, why));
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output))!);
                using var blocks = new HapBlocks();
                var packer = new HapPacker();
                var knobs = _knobs;
                var lastSaid = Stopwatch.StartNew();
                var request = new HapTranscodeRequest(job.Source, job.Output, codec)
                {
                    CopyAudio = job.CopyAudio,
                    AllowHardware = knobs.HwDecode,
                    Threads = Math.Max(1, knobs.CpuThreads),
                };
                var recipe = new Recipe(job.To, quality, job.Chunks, stage, job.RawBelow, job.CopyAudio ? "copy" : "none");
                var result = HapTranscode.Run(request, (ReadOnlySpan<byte> rgba, int w, int h, ref byte[] packet) =>
                {
                    encoder.Encode(rgba, w, h, codec, quality, blocks);
                    return packer.Pack(blocks, recipe.ChunksFor(w), recipe.SecondStage, recipe.RawBelowGain, ref packet);
                }, p =>
                {
                    _frames = p.Frames;
                    // Armed before the frame is told, so a knobs line sent on hearing it is never missed.
                    var hold = p.Frames == _holdAt;
                    if (hold)
                    {
                        _holdAt = 0;
                        _knobsHeard.Reset();
                    }
                    if (lastSaid.ElapsedMilliseconds >= 250 || p.Frames == 1)
                    {
                        Say(WorkerProtocol.Progress(job.Job, p.Frames, p.Expected, p.Fps));
                        lastSaid.Restart();
                    }
                    if (hold)
                        HoldForKnobs(cts.Token);
                }, ms => Pace(job.Job, ms, cts.Token), cts.Token);
                Say(WorkerProtocol.Done(job.Job, result.Frames, result.Quality, result.Audio));
            }
            catch (OperationCanceledException)
            {
                Say(WorkerProtocol.Failed(job.Job, "cancelled"));
            }
            catch (Exception ex) when (ex is MediaOpenException or FfmpegException or FfmpegUnavailableException or InvalidDataException
                                           or IOException or UnauthorizedAccessException or GlException or NotSupportedException or ArgumentException)
            {
                Say(WorkerProtocol.Failed(job.Job, ex.Message));
            }
            finally
            {
                lock (_write)
                {
                    _cancelCurrent = null;
                    _currentJob = "";
                }
            }
        }

        /// <summary>The test's hold (<see cref="HoldAtWord"/>): the frame goes on once the engine's next knobs line that holds is in.</summary>
        private void HoldForKnobs(CancellationToken ct) => _knobsHeard.Wait(ct);

        /// <summary>Between frames: hold while the knobs hold, saying so as the hold begins and ends; at a duty below one, rest so this
        /// frame's work is that share of its time.</summary>
        private void Pace(string job, double frameMs, CancellationToken ct)
        {
            if (!_go.IsSet)
            {
                Say(WorkerProtocol.Held(job, true, _frames));
                _go.Wait(ct);
                Say(WorkerProtocol.Held(job, false, _frames));
            }
            var duty = _knobs.GpuDuty;
            if (duty is > 0 and < 1)
            {
                var rest = frameMs * (1 / duty - 1);
                ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(rest, 10_000)));
                ct.ThrowIfCancellationRequested();
            }
        }

        public void Dispose()
        {
            _jobs.Dispose();
            _go.Dispose();
            _knobsHeard.Dispose();
            _stop.Dispose();
        }
    }
}
