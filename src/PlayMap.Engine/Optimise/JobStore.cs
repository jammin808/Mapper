using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Optimise;

/// <summary>A job's states: queued → encoding → verifying → done, or failed, or cancelled.</summary>
public static class JobStates
{
    public const string Queued = "queued";
    public const string Encoding = "encoding";
    public const string Verifying = "verifying";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsRunning(string state) => state is Encoding or Verifying;

    public static bool IsOver(string state) => state is Done or Failed or Cancelled;
}

/// <summary>One optimise job (node-local; never part of the project): the item it makes a version of, the source's hash, the
/// recipe and its key, its state, progress, attempts, the words that say why it is where it is, and the version it made.</summary>
public sealed record OptimiseJob(string Id, string Media, string Name, string Source, string Key, string Recipe, string To, string State, long CreatedUnixMs)
{
    public long Frames { get; init; }
    public long FramesDone { get; init; }
    public double Fps { get; init; }
    public int Attempts { get; init; }
    public string Words { get; init; } = "";
    public string Version { get; init; } = "";
    public double PsnrAvg { get; init; }
    public long FinishedUnixMs { get; init; }

    /// <summary>Held by MEDIA OPTIMISE PAUSE: a queued job is not started, a running one holds between frames.</summary>
    public bool Paused { get; init; }
}

/// <summary>The queue on disk: <c>NodeHome/optimise/jobs.json</c>, written whole on every state change through a temporary file
/// and a rename, so a power loss leaves the old list or the new one. A job that was running when the node stopped is queued again
/// on load, with the words why.</summary>
internal sealed class JobStore(string folder, ILogger log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Folder { get; } = folder;

    public string Path => System.IO.Path.Combine(Folder, "jobs.json");

    /// <summary>Where the jobs' partial files are written.</summary>
    public string Work => System.IO.Path.Combine(Folder, "work");

    public List<OptimiseJob> Load()
    {
        try
        {
            if (!File.Exists(Path))
                return [];
            var jobs = JsonSerializer.Deserialize<List<OptimiseJob>>(File.ReadAllText(Path), Json) ?? [];
            return [.. jobs.Select(j => JobStates.IsRunning(j.State)
                ? j with { State = JobStates.Queued, FramesDone = 0, Fps = 0, Words = "the node stopped while it ran: it starts again from its first frame" }
                : j)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.LogWarning(ex, "The optimise queue {Path} could not be read; it starts empty.", Path);
            return [];
        }
    }

    public void Save(IReadOnlyList<OptimiseJob> jobs)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = Path + ".new";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, jobs, Json);
            }
            DurableFile.MoveIntoPlace(temp, Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "The optimise queue {Path} could not be written.", Path);
        }
    }

    /// <summary>The partial file a job writes.</summary>
    public string PartialFor(string jobId) => System.IO.Path.Combine(Work, jobId + ".mov.part");
}
