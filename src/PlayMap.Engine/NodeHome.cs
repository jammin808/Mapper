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

    /// <summary>One read or write of a home's settings file at a time in this process: a load that puts the file right and a read beside
    /// it see the file before or after, never between.</summary>
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

    /// <summary>The node's settings, made on first run (a fresh id and pairing token) and put right when the file does not read (the
    /// damaged one kept beside it): a load that may write the file, so only the node's start and the settings file's one writer (the
    /// network host, inside its own lock from the read to the write) load it. Every other reader reads it as it is
    /// (<see cref="ReadSettings"/>).</summary>
    public NodeSettings LoadSettings(string defaultName)
    {
        NodeSettings read;
        lock (SettingsGate)
            read = LoadSettingsLocked(defaultName);
        SettingsRead?.Invoke();
        return read;
    }

    /// <summary>The node's settings as the file holds them now, read only: null when there is no file or it does not read as the node's
    /// settings. Nothing is made, put right or kept beside it (a reader in another process — the desk's pairing-token read — or beside
    /// the one writer never writes the file).</summary>
    public NodeSettings? ReadSettings()
    {
        string text;
        lock (SettingsGate)
        {
            if (!File.Exists(SettingsPath))
                return null;
            try
            {
                text = ReadText();
            }
            catch (FileNotFoundException)
            {
                // Gone between the look and the read: no settings to read.
                return null;
            }
        }
        return Parse(text);
    }

    /// <summary>Raised on the reader's thread after each read of the settings file, outside the file's own lock (a test holds an edit
    /// here, between its read and its write).</summary>
    internal event Action? SettingsRead;

    private Lock SettingsGate => SettingsGates.GetOrAdd(SettingsPath, _ => new Lock());

    private NodeSettings LoadSettingsLocked(string defaultName)
    {
        if (!File.Exists(SettingsPath))
        {
            var first = NodeSettings.CreateDefault(defaultName);
            SaveSettings(first);
            return first;
        }
        var text = ReadText();
        if (Parse(text) is { } loaded)
            return loaded;
        // A file that does not read is kept beside the new one so nothing is lost silently, and the new one keeps the
        // node's id, name and pairing token whenever the old one still says them: a typo never unpairs the show.
        File.Copy(SettingsPath, SettingsPath + ".damaged", overwrite: true);
        var fresh = KeepIdentity(NodeSettings.CreateDefault(defaultName), text);
        SaveSettings(fresh);
        return fresh;
    }

    /// <summary>The settings file's text. Under the file's gate.</summary>
    /// <remarks>Read as every durable file is read (shared for write and delete): a reader here or in another process (the desk's
    /// pairing-token read) never makes the writer's replace fail.</remarks>
    private string ReadText() => DurableFile.ReadAllText(SettingsPath);

    /// <summary>The settings a file's text holds, or null when it does not read as the node's settings (not JSON, or no valid node id).</summary>
    private static NodeSettings? Parse(string text)
    {
        try
        {
            // A key written as null (a hand edit) reads as a missing one does: a package's settings its defaults, a list empty.
            var loaded = WithoutNulls(text)?.Deserialize<NodeSettings>(Json);
            return loaded is not null && Ids.IsValid(loaded.NodeId) ? ProjectJson.Sanitize(loaded) : null;
        }
        catch (JsonException)
        {
            // Not the node's settings as they stand: the caller decides (a load puts it right, a read takes none).
            return null;
        }
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
            WriteFile(SettingsPath, text);
    }

    /// <summary>How the settings file's text is written: durably (<see cref="DurableFile.WriteAllText"/>). A test replaces it to stand
    /// for a disk that takes the rename and refuses the folder's flush after.</summary>
    internal Action<string, string> WriteFile { get; set; } = static (path, text) => DurableFile.WriteAllText(path, text);
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
            return JsonSerializer.Deserialize<RecoveryRecord>(DurableFile.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One playlist's transport at the instant the record was written.</summary>
public sealed record RecoveredLane(string PlaylistId, string ItemId, TransportState State, long PositionNs, double Rate, long AtUnixMs);
