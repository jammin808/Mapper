using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using PlayMap.Core.Align;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Align;

/// <summary>One output's measured correction as its files read: the warp grid (C × R nodes, u, v, valid), the blend (weight, pedestal,
/// gain at a quarter of the raster), the gains and the output's γ; its key changes whenever any of them does.</summary>
public sealed record MeasuredLoad(string OutputId, string Key, FloatMap? Grid, FloatMap? Blend, Vector4 Gains, float Gamma);

/// <summary>Every output's loaded correction, and the words for each output whose files are missing or changed.</summary>
public sealed record MeasuredSet(ImmutableDictionary<string, MeasuredLoad> Loads, ImmutableDictionary<string, string> Faults)
{
    public static readonly MeasuredSet Empty = new(ImmutableDictionary<string, MeasuredLoad>.Empty, ImmutableDictionary<string, string>.Empty);
}

/// <summary>
/// The outputs' measured corrections read from their files, off the control lane and off the render thread: when the project names
/// other files (or the project's folder moves), a pool task reads what changed — each file checked against its hash, a file a project
/// names opened only inside its folder — and publishes a new set whole, which the render host uploads on its own thread. An output
/// whose warp file is missing or changed runs without its correction, and the set says which file.
/// </summary>
public sealed class MeasuredLibrary
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FloatMap> _files = new(StringComparer.Ordinal);
    private volatile MeasuredSet _set = MeasuredSet.Empty;
    private string _wanted = "";
    private long _sequence;
    private Task _loading = Task.CompletedTask;

    public MeasuredSet Current => _set;

    /// <summary>Raised on the reading task after a new set is published.</summary>
    public event Action? Published;

    /// <summary>The latest load (tests wait on it).</summary>
    public Task Settled
    {
        get
        {
            lock (_gate)
                return _loading;
        }
    }

    /// <summary>Starts reading what the project names when that differs from what was read last; any thread.</summary>
    public void Refresh(Project project, string? folder)
    {
        ArgumentNullException.ThrowIfNull(project);
        var wanted = project.Outputs.Where(o => o.Measured is { Warp: not null } or { Blend: not null } or { Color: not null })
            .Select(o => (o.Id, Key: KeyOf(o.Measured!))).ToList();
        var signature = (folder ?? "") + "\n" + string.Join("\n", wanted.Select(w => w.Id + "=" + w.Key));
        lock (_gate)
        {
            if (signature == _wanted)
                return;
            _wanted = signature;
            var sequence = ++_sequence;
            var outputs = project.Outputs.Where(o => wanted.Any(w => w.Id == o.Id)).ToList();
            _loading = Task.Run(() => Load(sequence, outputs, folder));
        }
    }

    private static string KeyOf(MeasuredCorrection m) => string.Create(CultureInfo.InvariantCulture,
        $"{m.Warp?.File}|{m.Warp?.Hash}|{m.Blend?.File}|{m.Blend?.Hash}|{m.Color?.GainR}|{m.Color?.GainG}|{m.Color?.GainB}|{m.Color?.Level}|{m.Color?.Gamma}");

    private void Load(long sequence, List<Output> outputs, string? folder)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var loads = ImmutableDictionary.CreateBuilder<string, MeasuredLoad>(StringComparer.Ordinal);
        var faults = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var o in outputs)
        {
            var m = o.Measured!;
            FloatMap? grid = null;
            FloatMap? blend = null;
            var problems = new List<string>();
            if (m.Warp is { } warp)
            {
                used.Add(warp.File + "|" + warp.Hash);
                grid = Read(folder, warp.File, warp.Hash, out var why);
                if (grid is null)
                    problems.Add(why);
                else if (grid.Channels != 3 || grid.Width != warp.Columns || grid.Height != warp.Rows)
                {
                    problems.Add($"{warp.File} is not a {warp.Columns}×{warp.Rows} grid");
                    grid = null;
                }
            }
            if (m.Blend is { } b)
            {
                used.Add(b.File + "|" + b.Hash);
                blend = Read(folder, b.File, b.Hash, out var why);
                if (blend is null)
                    problems.Add(why);
                else if (blend.Channels != 3)
                {
                    problems.Add($"{b.File} is not a blend map");
                    blend = null;
                }
            }
            if (problems.Count > 0)
                faults[o.Id] = string.Join("; ", problems) + $" — {o.Name} runs without its measured correction";
            if (problems.Count > 0 && m.Warp is not null)
                continue;
            var c = m.Color;
            var gains = c is null ? Vector4.One : new Vector4((float)c.GainR, (float)c.GainG, (float)c.GainB, (float)(c.Level > 0 ? c.Level : 1));
            var gamma = c is { Gamma: > 0 } ? (float)c.Gamma : 2.2f;
            loads[o.Id] = new MeasuredLoad(o.Id, KeyOf(m), grid, blend, gains, gamma);
        }
        lock (_gate)
        {
            if (sequence != _sequence)
                return;
            _set = new MeasuredSet(loads.ToImmutable(), faults.ToImmutable());
            foreach (var key in _files.Keys.Where(k => !used.Contains(k)).ToList())
                _files.Remove(key);
        }
        Published?.Invoke();
    }

    /// <summary>A named file's map: read once per content (by hash), refused when missing, outside the folder or changed.</summary>
    private FloatMap? Read(string? folder, string file, string hash, out string why)
    {
        why = "";
        var key = file + "|" + hash;
        lock (_gate)
        {
            if (_files.TryGetValue(key, out var cached))
                return cached;
        }
        if (AlignArchive.Resolve(folder, file) is not { } path)
        {
            why = folder is null ? $"{file}: the project has no folder" : $"{file} lies outside the project's folder";
            return null;
        }
        if (!File.Exists(path))
        {
            why = $"{file} missing";
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!string.Equals(Pfm.Hash(bytes), hash, StringComparison.Ordinal))
            {
                why = $"{file} changed since it was measured";
                return null;
            }
            var map = Pfm.Read(bytes);
            lock (_gate)
                _files[key] = map;
            return map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            why = $"{file}: {ex.Message}";
            return null;
        }
    }
}
