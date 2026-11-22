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
    public NodeSettings LoadSettings(string defaultName) => LoadSettings(defaultName, out _);

    /// <summary><see cref="LoadSettings(string)"/>, saying which fields of the settings' own record the file lacked (by name, as the
    /// file spells them; never a value), each read by its rule (<see cref="FillMissing"/>). Empty for a file with every field, a first
    /// run and a file that does not read.</summary>
    internal NodeSettings LoadSettings(string defaultName, out ImmutableArray<string> missing)
    {
        NodeSettings read;
        lock (SettingsGate)
            read = LoadSettingsLocked(defaultName, out missing);
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
        // A file without its pairing token reads as no settings here: only the load makes the token that replaces it.
        return Parse(text, Environment.MachineName) is { TokenMissing: false } parsed ? parsed.Settings : null;
    }

    /// <summary>Raised on the reader's thread after each read of the settings file, outside the file's own lock (a test holds an edit
    /// here, between its read and its write).</summary>
    internal event Action? SettingsRead;

    private Lock SettingsGate => SettingsGates.GetOrAdd(SettingsPath, _ => new Lock());

    private NodeSettings LoadSettingsLocked(string defaultName, out ImmutableArray<string> missing)
    {
        missing = [];
        if (!File.Exists(SettingsPath))
        {
            var first = NodeSettings.CreateDefault(defaultName);
            SaveSettings(first);
            return first;
        }
        var text = ReadText();
        if (Parse(text, defaultName) is { } parsed)
        {
            missing = parsed.Missing;
            if (!parsed.TokenMissing)
                return parsed.Settings;
            // A file that lacks the pairing token is put right as a damaged one is (kept beside, the new one written), but it keeps
            // every field it does hold: only the token is made afresh, so the node never runs open for want of one.
            File.Copy(SettingsPath, SettingsPath + ".damaged", overwrite: true);
            SaveSettings(parsed.Settings);
            return parsed.Settings;
        }
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

    /// <summary>The settings a file's text holds, with the fields it lacked filled by their rule and named, or null when it does not
    /// read as the node's settings (not JSON, or no valid node id).</summary>
    private static ParsedSettings? Parse(string text, string defaultName)
    {
        try
        {
            // A key written as null (a hand edit) reads as a missing one does: a package's settings its defaults, a list empty.
            var root = WithoutNulls(text);
            var missing = root is System.Text.Json.Nodes.JsonObject obj ? FillMissing(obj, defaultName) : [];
            var loaded = root?.Deserialize<NodeSettings>(Json);
            return loaded is not null && Ids.IsValid(loaded.NodeId) ? new ParsedSettings(ProjectJson.Sanitize(loaded), missing) : null;
        }
        catch (JsonException)
        {
            // Not the node's settings as they stand: the caller decides (a load puts it right, a read takes none).
            return null;
        }
    }

    /// <summary>A settings file's settings as read, and the fields of the settings' own record its text lacked (by name).</summary>
    private sealed record ParsedSettings(NodeSettings Settings, ImmutableArray<string> Missing)
    {
        public bool TokenMissing => Missing.Contains(TokenKey);
    }

    private static readonly string NodeIdKey = JsonNamingPolicy.CamelCase.ConvertName(nameof(NodeSettings.NodeId));
    internal static readonly string TokenKey = JsonNamingPolicy.CamelCase.ConvertName(nameof(NodeSettings.Token));

    /// <summary>The keys of the settings' own record (its primary constructor's parameters, as the file spells them): a key the file
    /// lacks would otherwise read as its type's default, a null string or a default list.</summary>
    private static readonly ImmutableArray<string> PrimaryKeys =
    [
        .. typeof(NodeSettings).GetConstructors().Single().GetParameters().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!)),
    ];

    /// <summary>Fills each key of the settings' own record that <paramref name="root"/> lacks, and returns their names in the record's
    /// order. A string, the trusted list and a port read as a fresh node's; the name as <paramref name="defaultName"/> (as a first run
    /// and a damaged file name it); TrustLoopback, Announce and RequireHttps as the value that widens no access (no loopback trusted,
    /// no hello, plain HTTP refused off loopback); the pairing token afresh, never "" (open), and its caller keeps the file beside as a
    /// damaged one; the node id stays missing, so the file takes the damaged path. Package settings are left to
    /// their records' own defaults. The file is not written for a filled field: the next save by the one writer writes every field.</summary>
    private static ImmutableArray<string> FillMissing(System.Text.Json.Nodes.JsonObject root, string defaultName)
    {
        var missing = PrimaryKeys.Where(k => !root.ContainsKey(k)).ToImmutableArray();
        if (missing.IsEmpty)
            return missing;
        var rule = JsonSerializer.SerializeToNode(NodeSettings.CreateDefault(defaultName) with
        {
            TrustLoopback = false,
            Announce = false,
            RequireHttps = true,
        }, Json)!.AsObject();
        foreach (var key in missing.Where(k => k != NodeIdKey))
            root[key] = rule[key]!.DeepClone();
        return missing;
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
