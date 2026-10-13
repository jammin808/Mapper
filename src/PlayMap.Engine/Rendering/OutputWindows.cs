using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Rendering;

/// <summary>How an output's display assignment (<see cref="Output.DisplayKey"/>) names a monitor. A display's
/// key is its platform name when no other display has that name, else the name and its 1-based number
/// ("DELL U2415#2"). An assignment matches, in order: a key; a name (the first display with it); a 1-based
/// number ("2", "#2", "DISPLAY2", @"\\.\DISPLAY2"). An empty assignment names no display: the output renders
/// offscreen only (previews and health still work) — the default output never takes the desk's screen.</summary>
public static class DisplayMatch
{
    public static string KeyOf(MonitorInfo monitor, IReadOnlyList<MonitorInfo> all)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(all);
        var same = 0;
        foreach (var m in all)
            if (string.Equals(m.Name, monitor.Name, StringComparison.OrdinalIgnoreCase))
                same++;
        var name = string.IsNullOrWhiteSpace(monitor.Name) ? "Display" : monitor.Name;
        return same > 1 || string.IsNullOrWhiteSpace(monitor.Name) ? string.Create(CultureInfo.InvariantCulture, $"{name}#{monitor.Index + 1}") : name;
    }

    public static MonitorInfo? Find(string? displayKey, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (string.IsNullOrWhiteSpace(displayKey))
            return null;
        var key = displayKey.Trim();
        foreach (var m in monitors)
            if (string.Equals(KeyOf(m, monitors), key, StringComparison.OrdinalIgnoreCase))
                return m;
        foreach (var m in monitors)
            if (string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase))
                return m;
        var digits = key;
        foreach (var prefix in (ReadOnlySpan<string>)[@"\\.\DISPLAY", "DISPLAY", "#"])
        {
            if (digits.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[prefix.Length..];
                break;
            }
        }
        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
            foreach (var m in monitors)
                if (m.Index == n - 1)
                    return m;
        return null;
    }
}

/// <summary>The output windows, on the render thread: every enabled output of kind Display or Window whose
/// display assignment matches a monitor gets a window there — borderless over the whole monitor for a
/// Display, a movable window of the output's raster (capped to the monitor) for a Window; Ndi and Virtual
/// outputs never get one. <see cref="Sync"/> diffs the project against the windows by output id, monitor and
/// mode, attaching new windows to the renderer and detaching (then disposing) the ones that went. A window
/// the platform closed (the operator closed it) stays closed until its output or its monitor changes; a window
/// that failed to open is not retried until then either, and says why.</summary>
internal sealed class OutputWindows : IDisposable
{
    private readonly record struct WindowKey(int Monitor, int X, int Y, int Width, int Height, OutputKind Kind, string Title);

    private sealed class Entry
    {
        public required WindowKey Key { get; init; }
        public OutputWindow? Window { get; set; }
        public string Display { get; init; } = "";
        public string? Problem { get; set; }
    }

    private readonly RenderContext _context;
    private readonly RenderEngine _engine;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _gone = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public OutputWindows(RenderContext context, RenderEngine engine)
    {
        _context = context;
        _engine = engine;
    }

    /// <summary>The output that paces the loop (its swap waits for the vertical blank), or null.</summary>
    public string? Pacer { get; private set; }

    /// <summary>Windows open now.</summary>
    public int Open { get; private set; }

    public OutputWindow? WindowOf(string outputId) =>
        _entries.TryGetValue(outputId, out var e) ? e.Window : null;

    public string? DisplayOf(string outputId) =>
        _entries.TryGetValue(outputId, out var e) ? e.Display : null;

    public string? ProblemOf(string outputId) =>
        _entries.TryGetValue(outputId, out var e) ? e.Problem : null;

    /// <summary>Brings the windows in line with the project; true when a window opened or closed.</summary>
    public bool Sync(Project project, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(project);
        var changed = false;
        _seen.Clear();
        foreach (var output in project.Outputs)
        {
            if (Want(output, monitors) is not { } want)
                continue;
            _seen.Add(output.Id);
            if (_entries.TryGetValue(output.Id, out var e))
            {
                if (e.Key == want.Key)
                    continue;
                changed |= Close(output.Id, e);
            }
            changed |= OpenWindow(output, want.Key, want.Display);
        }
        foreach (var id in _entries.Keys)
            if (!_seen.Contains(id))
                _gone.Add(id);
        foreach (var id in _gone)
            changed |= Close(id, _entries[id]);
        _gone.Clear();
        return changed;
    }

    /// <summary>Pumps the platform's events and closes the windows the platform closed; true when one closed.</summary>
    public bool Pump()
    {
        if (Open == 0)
            return false;
        _context.PumpEvents();
        var closed = false;
        foreach (var (id, e) in _entries)
        {
            if (e.Window is not { IsClosing: true } window)
                continue;
            _engine.Detach(id);
            window.Dispose();
            e.Window = null;
            e.Problem = "its window was closed — change the output or its display to open it again";
            Open--;
            closed = true;
        }
        if (closed)
            RepickPacer();
        return closed;
    }

    private static (WindowKey Key, string Display)? Want(Output output, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!output.Enabled || output.Kind is not (OutputKind.Display or OutputKind.Window) || output.Width < 1 || output.Height < 1)
            return null;
        if (DisplayMatch.Find(output.DisplayKey, monitors) is not { } m)
            return null;
        var key = output.Kind == OutputKind.Display
            ? new WindowKey(m.Index, m.X, m.Y, m.Width, m.Height, output.Kind, output.Name)
            : new WindowKey(m.Index, m.X, m.Y, Math.Min(output.Width, Math.Max(1, m.Width)), Math.Min(output.Height, Math.Max(1, m.Height)), output.Kind, output.Name);
        return (key, DisplayMatch.KeyOf(m, monitors));
    }

    private bool OpenWindow(Output output, WindowKey key, string display)
    {
        var entry = new Entry { Key = key, Display = display };
        _entries[output.Id] = entry;
        try
        {
            var options = key.Kind == OutputKind.Display
                ? new OutputWindowOptions(output.Name, key.Monitor, Borderless: true, VSync: true)
                : new OutputWindowOptions(output.Name, key.Monitor, Borderless: false, VSync: true, Width: key.Width, Height: key.Height, X: 32, Y: 32);
            entry.Window = OutputWindow.Create(_context, options);
            _engine.Attach(output.Id, entry.Window);
            Open++;
            RepickPacer();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException or Render.Gpu.GlException)
        {
            entry.Problem = $"its window could not open on {display}: {ex.Message}";
            _context.MakeCurrent();
            return false;
        }
    }

    private bool Close(string outputId, Entry e)
    {
        _entries.Remove(outputId);
        if (e.Window is null)
            return false;
        _engine.Detach(outputId);
        e.Window.Dispose();
        Open--;
        RepickPacer();
        return true;
    }

    /// <summary>The renderer paces on the first window attached that is still attached; this follows it.</summary>
    private void RepickPacer()
    {
        Pacer = null;
        foreach (var (id, e) in _entries)
        {
            if (e.Window is { SwapInterval: 1 })
            {
                Pacer = id;
                return;
            }
        }
    }

    public void Dispose()
    {
        foreach (var (id, e) in _entries)
        {
            if (e.Window is null)
                continue;
            _engine.Detach(id);
            e.Window.Dispose();
        }
        _entries.Clear();
        Open = 0;
        Pacer = null;
    }
}
