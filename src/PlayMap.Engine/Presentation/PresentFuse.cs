using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Presentation;

/// <summary>A fuse as its file holds it: the path it guards, how many starts armed it in a row, and when the last did (unix ms).</summary>
public sealed record FuseState(string Path, int Attempt, long AtUnixMs);

/// <summary>The present fuse (Patterns' fuse): <c>&lt;node home&gt;/playmap.present.fuse</c> is written before the DXGI platform starts
/// and deleted once every display output reached its first settled frames. A fuse still there at the next start means that start
/// never got its outputs up (a crash, a hang, a driver that never presented): the node runs OpenGL and says so until the operator
/// asks again (PRESENT PATH DXGI or PRESENT RESET). File operations only; callers are the render host's start and the control lane.</summary>
public sealed class PresentFuse(string folder, Func<long> unixMs)
{
    public const string FileName = "playmap.present.fuse";

    public string FilePath { get; } = Path.Combine(folder, FileName);

    /// <summary>The fuse, or null when there is none; a file that cannot be read counts as one attempt (it was written by a start).</summary>
    public FuseState? Read()
    {
        if (!File.Exists(FilePath))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var r = doc.RootElement;
            return new FuseState(r.TryGetProperty("path", out var p) ? p.GetString() ?? "dxgi" : "dxgi",
                r.TryGetProperty("attempt", out var a) && a.TryGetInt32(out var n) ? Math.Max(1, n) : 1,
                r.TryGetProperty("at", out var t) && t.TryGetInt64(out var at) ? at : 0);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new FuseState("dxgi", 1, 0);
        }
    }

    /// <summary>Writes the fuse before a start of <paramref name="path"/>: one attempt more than the fuse already held. It is on the disk
    /// before the start begins, so a start that hangs the machine until its power is cut still finds it at the next start.</summary>
    public FuseState Arm(string path)
    {
        var next = new FuseState(path, (Read()?.Attempt ?? 0) + 1, unixMs());
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        DurableFile.WriteAllText(FilePath, string.Create(CultureInfo.InvariantCulture,
            $$"""{"path":"{{next.Path}}","attempt":{{next.Attempt}},"at":{{next.AtUnixMs}}}"""));
        return next;
    }

    /// <summary>Deletes the fuse; true when there was one.</summary>
    public bool Clear()
    {
        if (!File.Exists(FilePath))
            return false;
        File.Delete(FilePath);
        return true;
    }
}

/// <summary>Which path a start takes: what was asked, unless the fuse says the last DXGI start never reached its first frames — then
/// OpenGL, with the words that say so and how to try again. Pure.</summary>
public static class PresentChoice
{
    /// <summary>The path word ("auto", "gl", "dxgi") a start takes and the words why when it is not the one asked ("" otherwise).</summary>
    public static (string Path, string Words) Decide(string asked, FuseState? fuse)
    {
        var path = Normalise(asked);
        if (path == "dxgi" && fuse is { Attempt: >= 1 } f)
        {
            var at = f.AtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(f.AtUnixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                : "an unknown time";
            return ("gl", string.Create(CultureInfo.InvariantCulture,
                $"the DXGI path did not reach its first frames last start ({f.Attempt} attempt{(f.Attempt == 1 ? "" : "s")}, at {at}); running OpenGL. PRESENT PATH DXGI or PRESENT RESET tries it again"));
        }
        return (path, "");
    }

    /// <summary>"auto", "gl" or "dxgi" (anything else reads as auto).</summary>
    public static string Normalise(string? path)
    {
        var p = (path ?? "").Trim().ToLowerInvariant();
        return p is "gl" or "dxgi" ? p : "auto";
    }
}

/// <summary>The node's present settings as this machine keeps them (NodeSettings.Present, saved by PRESENT PATH) and its fuse: what the
/// next render host start asks for. Control lane.</summary>
public sealed class PresentNode
{
    private readonly Action<PresentSettings> _save;
    private PresentSettings _saved;

    public PresentNode(PresentSettings saved, Action<PresentSettings> save, PresentFuse fuse)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(fuse);
        _saved = saved;
        _save = save;
        Fuse = fuse;
    }

    public PresentFuse Fuse { get; }

    private FuseState? _fuse;
    private bool _fuseRead;

    /// <summary>The fuse file as last read (read once on first ask, again after <see cref="RefreshFuse"/>).</summary>
    public FuseState? FuseNow
    {
        get
        {
            if (!_fuseRead)
                RefreshFuse();
            return _fuse;
        }
    }

    /// <summary>Reads the fuse file again (after a verb or the render host changed it).</summary>
    public void RefreshFuse()
    {
        _fuse = Fuse.Read();
        _fuseRead = true;
    }

    /// <summary>The settings saved now (the next start's).</summary>
    public PresentSettings Saved => Volatile.Read(ref _saved);

    public void Save(PresentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _save(settings);
        Volatile.Write(ref _saved, settings);
    }
}
