using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;

namespace PlayMap.Render.Effects;

/// <summary>An effect kind's files: the fragment file with its header, the vertex file or null, and imported images by buffer
/// name (base64 PNG or JPEG). <see cref="Hash"/> is the files' content identity (<see cref="FxCatalog.ContentHash"/>), the same in
/// every catalogue that holds them, so a renderer keeps its program and buffers across catalogues.</summary>
public sealed record FxSource(string Fragment, string? Vertex, ImmutableSortedDictionary<string, string> Images)
{
    public string Hash { get; } = FxCatalog.ContentHash(Fragment, Vertex);
}

/// <summary>Every effect kind a show can use: the built-in library (ISF files written for PlayMap, embedded, MIT) and the show's
/// own ISF files ("isf:&lt;asset id&gt;"). Immutable; one per set of assets, made again only when the show's assets change, so
/// the render thread and the lane read the same kinds without locks. Translations are made once per kind and dialect. The last
/// few catalogues are kept, so readers of different projects in one process (two engines, a draft beside the published show)
/// never make one per frame.</summary>
public sealed class FxCatalog
{
    /// <summary>How many catalogues <see cref="For"/> keeps, most recently used first.</summary>
    private const int Recent = 4;

    /// <summary>The built-in kinds: id, embedded file, name, family.</summary>
    private static readonly (string Id, string File, string Name, string Family)[] Library =
    [
        ("fx.invert", "invert.fs", "Invert", "colour"),
        ("fx.levels", "levels.fs", "Levels", "colour"),
        ("fx.hsl", "hsl.fs", "Hue, saturation, lightness", "colour"),
        ("fx.threshold", "threshold.fs", "Threshold", "colour"),
        ("fx.blur", "blur.fs", "Blur", "blur"),
        ("fx.vignette", "vignette.fs", "Vignette", "look"),
        ("fx.pixelate", "pixelate.fs", "Pixelate", "look"),
        ("fx.mirror", "mirror.fs", "Mirror", "geometry"),
        ("fx.tile", "tile.fs", "Tile", "geometry"),
        ("fx.feedback", "feedback.fs", "Feedback", "time"),
    ];

    private static readonly ImmutableSortedDictionary<string, string> NoImages = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    private static readonly Lazy<FxCatalog> BuiltinCatalog = new(LoadBuiltins);

    private static readonly FxCatalog?[] RecentCatalogs = new FxCatalog?[Recent];

    private static readonly Lock RecentGate = new();

    private readonly Dictionary<string, FxKind> _byId;
    private readonly Dictionary<string, FxSource> _sources;
    private readonly Dictionary<(string, IsfDialect), IsfProgramSource> _translations = [];
    private readonly Lock _gate = new();

    private FxCatalog(ImmutableArray<FxAsset> assets, ImmutableArray<FxKind> kinds, Dictionary<string, FxSource> sources, int builtins)
    {
        Assets = assets;
        Kinds = kinds;
        _sources = sources;
        _byId = kinds.ToDictionary(k => k.Id, StringComparer.Ordinal);
        BuiltinCount = builtins;
    }

    /// <summary>The library alone (no show assets).</summary>
    public static FxCatalog Builtin => BuiltinCatalog.Value;

    /// <summary>The assets this catalogue was made from (compared by reference to reuse it).</summary>
    public ImmutableArray<FxAsset> Assets { get; }

    /// <summary>Every kind: the library first, then the show's ISF files in their order.</summary>
    public ImmutableArray<FxKind> Kinds { get; }

    public int BuiltinCount { get; }

    public int IsfCount => Kinds.Length - BuiltinCount;

    /// <summary>The catalogue for a show's assets: a recent one made from the same array, else a new one. Any thread; allocates
    /// nothing when the assets are recent.</summary>
    public static FxCatalog For(ImmutableArray<FxAsset> assets)
    {
        if (assets.IsDefaultOrEmpty)
            return Builtin;
        lock (RecentGate)
        {
            if (TakeRecent(assets) is { } known)
                return known;
        }
        var made = Make(assets);
        lock (RecentGate)
        {
            // Another thread may have made the same one meanwhile: every reader keeps one catalogue per set of assets.
            if (TakeRecent(assets) is { } raced)
                return raced;
            Array.Copy(RecentCatalogs, 0, RecentCatalogs, 1, Recent - 1);
            RecentCatalogs[0] = made;
        }
        return made;
    }

    /// <summary>The recent catalogue made from these assets, moved to the front; null when there is none. Under the gate.</summary>
    private static FxCatalog? TakeRecent(ImmutableArray<FxAsset> assets)
    {
        for (var i = 0; i < Recent; i++)
        {
            if (RecentCatalogs[i] is not { } c || c.Assets != assets)
                continue;
            if (i > 0)
            {
                Array.Copy(RecentCatalogs, 0, RecentCatalogs, 1, i);
                RecentCatalogs[0] = c;
            }
            return c;
        }
        return null;
    }

    private static FxCatalog Make(ImmutableArray<FxAsset> assets)
    {
        var builtin = Builtin;
        var kinds = ImmutableArray.CreateBuilder<FxKind>();
        kinds.AddRange(builtin.Kinds);
        var sources = new Dictionary<string, FxSource>(builtin._sources, StringComparer.Ordinal);
        foreach (var a in assets)
        {
            var id = AssetKindId(a.Id);
            var doc = IsfParser.Parse(a.Source);
            kinds.Add(FxKinds.FromIsf(id, FxFormat.Isf, a.Name, "isf", doc.Description, doc));
            sources[id] = new FxSource(a.Source, a.VertexSource.Length > 0 ? a.VertexSource : null, a.Images);
        }
        return new FxCatalog(assets, kinds.ToImmutable(), sources, builtin.BuiltinCount);
    }

    /// <summary>The kind id of a show's ISF asset.</summary>
    public static string AssetKindId(string assetId) => "isf:" + assetId;

    public FxKind? Find(string? id) => id is not null && _byId.TryGetValue(id, out var k) ? k : null;

    /// <summary>A kind by id, or by its name or the id's last word ("blur" finds fx.blur), ignoring case.</summary>
    public FxKind? FindLoose(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (Find(key) is { } exact)
            return exact;
        return Kinds.FirstOrDefault(k => k.Id.Equals(key, StringComparison.OrdinalIgnoreCase) || k.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
            || (k.Id.StartsWith("fx.", StringComparison.Ordinal) && k.Id[3..].Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    public FxSource? Source(string id) => _sources.TryGetValue(id, out var s) ? s : null;

    /// <summary>A kind's program sources for a dialect, translated once.</summary>
    public IsfProgramSource? Translation(string id, IsfDialect dialect)
    {
        if (Find(id) is not { } kind || Source(id) is not { } source)
            return null;
        lock (_gate)
        {
            if (_translations.TryGetValue((id, dialect), out var t))
                return t;
            t = IsfTranslator.Translate(kind.Document, source.Vertex, dialect);
            _translations[(id, dialect)] = t;
            return t;
        }
    }

    /// <summary>The SHA-256 (lowercase hex) of an ISF file's fragment and vertex sources: an asset's content identity.</summary>
    public static string ContentHash(string fragment, string? vertex)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var bytes = Encoding.UTF8.GetBytes(fragment + "\u0000" + (vertex ?? ""));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>The library's file text by its file name (for a test, or to duplicate a built-in into the show).</summary>
    public static string LibraryFile(string file)
    {
        var name = "PlayMap.Render.Effects.Library." + file;
        using var stream = typeof(FxCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"the effect library has no {file}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FxCatalog LoadBuiltins()
    {
        var kinds = ImmutableArray.CreateBuilder<FxKind>();
        var sources = new Dictionary<string, FxSource>(StringComparer.Ordinal);
        foreach (var (id, file, name, family) in Library)
        {
            var text = LibraryFile(file);
            var doc = IsfParser.Parse(text);
            if (!doc.Valid)
                throw new InvalidOperationException($"the built-in {id} does not read: {doc.Issues[0].Words}");
            kinds.Add(FxKinds.FromIsf(id, FxFormat.Builtin, name, family, doc.Description, doc));
            sources[id] = new FxSource(text, null, NoImages);
        }
        return new FxCatalog([], kinds.ToImmutable(), sources, kinds.Count);
    }
}
