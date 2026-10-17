using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Engine.Playback;
using PlayMap.Core.Storage;

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

    /// <summary>One read or write of a home's settings file at a time in this process: Windows refuses to replace a file another handle
    /// has open, so a read beside a write's rename would fail the write.</summary>
    private static readonly ConcurrentDictionary<string, Lock> SettingsGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        lock (SettingsGate)
            return LoadSettingsLocked(defaultName);
    }

    private Lock SettingsGate => SettingsGates.GetOrAdd(SettingsPath, _ => new Lock());

    private NodeSettings LoadSettingsLocked(string defaultName)
    {
        if (!File.Exists(SettingsPath))
        {
            var first = NodeSettings.CreateDefault(defaultName);
            SaveSettings(first);
            return first;
        }
        string text;
        // Shared for write and delete: a reader never refuses a writer's own open of the file. Within this process the gate keeps a
        // read and a replace apart; a reader in another process (the desk's pairing-token read) can still make Windows refuse a
        // replace, which the writer answers in words.
        using (var file = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(file, Encoding.UTF8))
            text = reader.ReadToEnd();
        try
        {
            // A key written as null (a hand edit) reads as a missing one does: a package's settings its defaults, a list empty.
            var loaded = WithoutNulls(text)?.Deserialize<NodeSettings>(Json);
            if (loaded is not null && Ids.IsValid(loaded.NodeId))
                return ProjectJson.Sanitize(loaded);
        }
        catch (JsonException)
        {
            // Falls through: the file is kept and the node keeps who it is.
        }
        // A file that does not read is kept beside the new one so nothing is lost silently, and the new one keeps the
        // node's id, name and pairing token whenever the old one still says them: a typo never unpairs the show.
        File.Copy(SettingsPath, SettingsPath + ".damaged", overwrite: true);
        var fresh = KeepIdentity(NodeSettings.CreateDefault(defaultName), text);
        SaveSettings(fresh);
        return fresh;
    }

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The settings' JSON with every null-valued key removed, at any depth, so it reads as missing.</summary>
    private static System.Text.Json.Nodes.JsonNode? WithoutNulls(string text)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: Lenient);
        Prune(root);
        return root;

        static void Prune(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                foreach (var key in obj.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                    obj.Remove(key);
                foreach (var kv in obj)
                    Prune(kv.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray arr)
            {
                foreach (var item in arr)
                    Prune(item);
            }
        }
    }

    /// <summary>The node's id, name and pairing token from a settings file that no longer reads as a whole: from its
    /// JSON when that parses, else from the text of the three keys.</summary>
    private static NodeSettings KeepIdentity(NodeSettings fresh, string text)
    {
        string? id = null, name = null, token = null;
        try
        {
            using var doc = JsonDocument.Parse(text, Lenient);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String)
                        continue;
                    if (p.Name.Equals("nodeId", StringComparison.OrdinalIgnoreCase))
                        id = p.Value.GetString();
                    else if (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                        name = p.Value.GetString();
                    else if (p.Name.Equals("token", StringComparison.OrdinalIgnoreCase))
                        token = p.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            id = Quoted(text, "nodeId");
            name = Quoted(text, "name");
            token = Quoted(text, "token");
        }
        if (Ids.IsValid(id))
            fresh = fresh with { NodeId = id! };
        if (!string.IsNullOrWhiteSpace(name))
            fresh = fresh with { Name = name };
        if (token is not null)
            fresh = fresh with { Token = token };
        return fresh;

        static string? Quoted(string text, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"\\\\]*)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    public void SaveSettings(NodeSettings settings)
    {
        var text = JsonSerializer.Serialize(settings, Json);
        lock (SettingsGate)
            DurableFile.WriteAllText(SettingsPath, text);
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
        DurableFile.WriteAllText(path, JsonSerializer.Serialize(this, Json));
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
