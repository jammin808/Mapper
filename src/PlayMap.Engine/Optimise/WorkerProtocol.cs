using System.Text;
using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Optimise;

/// <summary>One job as the engine hands it to the worker: the source file, the partial output it writes, the recipe's target,
/// quality, chunks, second stage and raw threshold, whether audio is copied.</summary>
public sealed record WorkerJob(string Job, string Source, string Output, string To, string Quality, int Chunks, string SecondStage, double RawBelow, bool CopyAudio);

/// <summary>The worker's pace: the governor's state word ("run", "trickle", "paused", "waiting"), its GPU duty, decoder threads
/// and whether hardware decode is allowed. Paused and waiting hold the job between frames.</summary>
public sealed record WorkerKnobs(string Mode, double GpuDuty, int CpuThreads, bool HwDecode)
{
    public bool Holds => Mode is "paused" or "waiting";
}

/// <summary>A line from the worker: hello (pid, GL, encoder id), progress, held (on or off, at a frame), done (frames, quality, audio words), failed (words) or
/// cancelled. Unknown kinds are kept as such and ignored by the supervisor (fail closed: they change nothing).</summary>
public sealed record WorkerEvent(string Kind, string Job)
{
    public int Pid { get; init; }
    public string Gl { get; init; } = "";
    public string Encoder { get; init; } = "";
    public long Frames { get; init; }
    public long Of { get; init; }
    public double Fps { get; init; }
    public VersionQuality Quality { get; init; } = VersionQuality.None;
    public string Audio { get; init; } = "";
    public string Words { get; init; } = "";

    /// <summary>A held line: true when the worker holds the job between frames, false when it goes on.</summary>
    public bool On { get; init; }
}

/// <summary>The JSON lines between the engine and its optimise worker (one object a line, "t" its kind). No secret rides them:
/// nothing the worker does needs one.</summary>
public static class WorkerProtocol
{
    public static string Run(WorkerJob job) => Line(w =>
    {
        w.WriteString("t", "run");
        w.WriteString("job", job.Job);
        w.WriteString("source", job.Source);
        w.WriteString("output", job.Output);
        w.WriteString("to", job.To);
        w.WriteString("quality", job.Quality);
        w.WriteNumber("chunks", job.Chunks);
        w.WriteString("secondStage", job.SecondStage);
        w.WriteNumber("rawBelow", job.RawBelow);
        w.WriteBoolean("audio", job.CopyAudio);
    });

    public static string Knobs(WorkerKnobs k) => Line(w =>
    {
        w.WriteString("t", "knobs");
        w.WriteString("mode", k.Mode);
        w.WriteNumber("gpuDuty", k.GpuDuty);
        w.WriteNumber("cpuThreads", k.CpuThreads);
        w.WriteBoolean("hw", k.HwDecode);
    });

    public static string Hello(int pid, string gl, string encoder) => Line(w =>
    {
        w.WriteString("t", "hello");
        w.WriteNumber("pid", pid);
        w.WriteString("gl", gl);
        w.WriteString("encoder", encoder);
    });

    public static string Progress(string job, long frames, long of, double fps) => Line(w =>
    {
        w.WriteString("t", "progress");
        w.WriteString("job", job);
        w.WriteNumber("frames", frames);
        w.WriteNumber("of", of);
        w.WriteNumber("fps", Math.Round(fps, 1));
    });

    /// <summary>The worker holds the job between frames (<paramref name="on"/>) or goes on with it, after <paramref name="frames"/>.</summary>
    public static string Held(string job, bool on, long frames) => Line(w =>
    {
        w.WriteString("t", "held");
        w.WriteString("job", job);
        w.WriteBoolean("on", on);
        w.WriteNumber("frames", frames);
    });

    public static string Done(string job, long frames, VersionQuality q, string audio) => Line(w =>
    {
        w.WriteString("t", "done");
        w.WriteString("job", job);
        w.WriteNumber("frames", frames);
        w.WriteStartObject("quality");
        w.WriteNumber("psnrAvg", q.PsnrAvg);
        w.WriteNumber("psnrMin", q.PsnrMin);
        w.WriteNumber("sampled", q.Sampled);
        w.WriteNumber("worstAtMs", q.WorstAtMs);
        w.WriteEndObject();
        w.WriteString("audio", audio);
    });

    public static string Failed(string job, string words) => Line(w =>
    {
        w.WriteString("t", "failed");
        w.WriteString("job", job);
        w.WriteString("words", words);
    });

    /// <summary>Reads one line from the worker; null when it is not a protocol line (the supervisor ignores it).</summary>
    public static WorkerEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("t", out var t))
                return null;
            var e = new WorkerEvent(t.GetString() ?? "", Str(r, "job"))
            {
                Pid = r.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
                Gl = Str(r, "gl"),
                Encoder = Str(r, "encoder"),
                Frames = r.TryGetProperty("frames", out var f) ? f.GetInt64() : 0,
                Of = r.TryGetProperty("of", out var of) ? of.GetInt64() : 0,
                Fps = r.TryGetProperty("fps", out var fps) ? fps.GetDouble() : 0,
                Audio = Str(r, "audio"),
                Words = Str(r, "words"),
                On = r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
            };
            if (r.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object)
            {
                e = e with
                {
                    Quality = new VersionQuality(q.GetProperty("psnrAvg").GetDouble(), q.GetProperty("psnrMin").GetDouble(),
                        q.GetProperty("sampled").GetInt32(), q.GetProperty("worstAtMs").GetInt64()),
                };
            }
            return e;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Reads a run line in the worker; null when the line is not one.</summary>
    public static WorkerJob? ParseRun(JsonElement r)
    {
        try
        {
            return new WorkerJob(Str(r, "job"), Str(r, "source"), Str(r, "output"), Str(r, "to"), Str(r, "quality"),
                r.GetProperty("chunks").GetInt32(), Str(r, "secondStage"), r.GetProperty("rawBelow").GetDouble(), r.GetProperty("audio").GetBoolean());
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    public static WorkerKnobs? ParseKnobs(JsonElement r)
    {
        try
        {
            return new WorkerKnobs(Str(r, "mode"), r.GetProperty("gpuDuty").GetDouble(), r.GetProperty("cpuThreads").GetInt32(), r.GetProperty("hw").GetBoolean());
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static string Str(JsonElement r, string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Line(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
