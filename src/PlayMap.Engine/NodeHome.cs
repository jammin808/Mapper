using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Engine.Playback;

namespace PlayMap.Engine;

/// <summary>The node's own folder: its settings (never inside a project — a project travels, a pairing token
/// and a bind address do not), its journal, its recovery record, its media store and its keys.</summary>
public sealed class NodeHome
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public NodeHome(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public string SettingsPath => Path.Combine(Folder, "playmap.node.json");
    public string JournalPath => Path.Combine(Folder, "playmap.journal.jsonl");
    public string RecoveryPath => Path.Combine(Folder, "playmap.recovery.json");
    public string MediaStore => Path.Combine(Folder, "media");
    public string KeysFolder => Path.Combine(Folder, "keys");
    public string ModelsFolder => Path.Combine(Folder, "models");

    /// <summary>The default home: the user's local application data, PlayMap.</summary>
    public static NodeHome Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PlayMap"));

    /// <summary>The node's settings, made on first run (a fresh id and pairing token) and kept.</summary>
    public NodeSettings LoadSettings(string defaultName)
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<NodeSettings>(File.ReadAllText(SettingsPath), Json);
                // A key written as null (a hand edit) reads as a missing one does: a package's settings its defaults, a list empty.
                if (loaded is not null && Ids.IsValid(loaded.NodeId))
                    return ProjectJson.Sanitize(loaded);
            }
            catch (JsonException)
            {
                // A damaged settings file is kept beside the new one so nothing is lost silently.
                File.Copy(SettingsPath, SettingsPath + ".damaged", overwrite: true);
            }
        }
        var fresh = NodeSettings.CreateDefault(defaultName);
        SaveSettings(fresh);
        return fresh;
    }

    public void SaveSettings(NodeSettings settings)
    {
        var temp = SettingsPath + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}

/// <summary>What a restart puts back: the project (its path, or its whole JSON when unsaved), the air, and each
/// playlist's transport as a wall-clock anchor — so a node that restarts mid-show seeks to now and carries on.</summary>
public sealed record RecoveryRecord(
    long WrittenUnixMs,
    string? ProjectPath,
    string? UnsavedProjectJson,
    bool Blackout,
    string? Pattern,
    string? FocusedPlaylist,
    ImmutableArray<RecoveredLane> Lanes)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Each package's runtime facts kept across a restart, one JSON object per package key.</summary>
    public ImmutableSortedDictionary<string, string> Modules { get; init; } = ImmutableSortedDictionary<string, string>.Empty;

    public static RecoveryRecord Capture(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var snap = engine.Snapshot;
        var wallNow = engine.Services.Wall.UnixMs;
        var leaderNow = engine.LeaderNowNs;
        var lanes = snap.Playback.Playlists.Values
            .Where(l => !l.IsIdle)
            .Select(l =>
            {
                var (_, clip, itemId) = l.At(leaderNow);
                return new RecoveredLane(l.PlaylistId, itemId ?? "", clip.State, clip.PositionAt(leaderNow), clip.Anchor.Rate, wallNow);
            })
            .ToImmutableArray();
        return new RecoveryRecord(wallNow, engine.ProjectPath, engine.ProjectPath is null || engine.Dirty ? ProjectJson.SerializeEntity(snap.Project.Project) : null,
            snap.Air.Blackout, snap.Air.PatternKind, snap.Playback.FocusedId, lanes)
        {
            Modules = engine.SaveModuleRecovery(),
        };
    }

    public void Save(string path)
    {
        var temp = path + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static RecoveryRecord? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One playlist's transport at the instant the record was written.</summary>
public sealed record RecoveredLane(string PlaylistId, string ItemId, TransportState State, long PositionNs, double Rate, long AtUnixMs);
