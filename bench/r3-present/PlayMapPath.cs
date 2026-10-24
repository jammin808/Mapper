using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using PlayMap.Core.Presentation;
using PlayMap.Core.Time;

namespace PlayMap.Bench.Present;

/// <summary>One of PlayMap's own present paths, as the engine opens it: the GL window (GLFW on WGL, <c>PRESENT PATH GL</c>, AUTO's
/// path) or the Direct3D 11 path (a DXGI flip swap chain on a D3D11 device through ANGLE, <c>PRESENT PATH DXGI</c>). A presenter
/// borderless over the run's display shows one of two pictures made once — black, or black with the white quad — and the output's
/// present clock reads what the platform tells it, as the engine's does: each frame the picture is recorded into the device's frame
/// list, the list submitted, and the presenter presents; each present's QPC instants and the platform's present count go to the
/// record, and the clock's words close it.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal sealed class PlayMapPath : IDisposable
{
    private readonly IGpuPlatform _platform;
    private readonly IOutputPresenter _presenter;
    private readonly GpuTexture _flash;
    private readonly GpuTexture _dark;
    private readonly PresentClock _clock;

    public PlayMapPath(string name, IGpuPlatform platform, BenchDisplay display, PresentRecord record)
    {
        Name = name;
        _platform = platform;
        var monitors = platform.Monitors();
        var monitor = monitors.FirstOrDefault(m => m.X == display.X && m.Y == display.Y)
            ?? (display.Index < monitors.Count ? monitors[display.Index] : null);
        _presenter = platform.CreatePresenter(new OutputPresenterOptions($"PlayMap bench: {name}", monitor, Borderless: true,
            display.Width, display.Height, display.X, display.Y));
        var (w, h) = (_presenter.Width, _presenter.Height);
        var device = platform.Device;
        var desc = new TextureDesc(w, h, TextureFormat.Rgba8, TextureUsage.Sampled | TextureUsage.CopyDest, TextureFilter.Nearest);
        _flash = device.CreateTexture(desc with { Name = "bench flash" });
        _dark = device.CreateTexture(desc with { Name = "bench dark" });
        var pixels = new byte[w * h * 4];
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        var list = device.BeginFrame(0);
        list.Upload(_dark, pixels, UploadFormat.Rgba8);
        var quad = display.Quad;
        for (var y = Math.Max(0, quad.top); y < Math.Min(h, quad.bottom); y++)
            pixels.AsSpan((y * w + Math.Max(0, quad.left)) * 4, (Math.Min(w, quad.right) - Math.Max(0, quad.left)) * 4).Fill(255);
        list.Upload(_flash, pixels, UploadFormat.Rgba8);
        device.WaitFor(device.Submit(list));
        _clock = new PresentClock(monitor is { RefreshHz: > 0 } m ? m.RefreshHz : 60, measured: true, platform.Path);
        record.Note(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {platform.Name} on {platform.Api}{(platform.Adapter.Length > 0 ? " · " + platform.Adapter : "")}, {w}x{h} on {monitor?.Name ?? display.Name}, " +
            $"swap interval {_presenter.SwapInterval}{(platform.Words.Length > 0 ? "; " + platform.Words : "")}"));
    }

    public string Name { get; }

    public bool Closed => _presenter.IsClosing;

    public void Frame(long frame, bool white, PresentRecord record)
    {
        _clock.BeginFrame(MonoClock.Instance.NowNs);
        var device = _platform.Device;
        var list = device.BeginFrame(frame);
        _presenter.Record(list, white ? _flash : _dark);
        device.Submit(list);
        var before = Stopwatch.GetTimestamp();
        var result = _presenter.Present();
        var after = Stopwatch.GetTimestamp();
        _clock.OnPresented(in result);
        _presenter.Observe(_clock);
        record.Row(Name, frame, white, before, after, result.PresentCount, 0, 0, 0,
            result.Queued ? "queued" : result.Problem.ToString().ToLowerInvariant());
        _platform.PumpEvents();
    }

    /// <summary>What the output's clock made of the run: its mode, evidence and lateness, in the engine's words.</summary>
    public string Words() => _clock.Snapshot(MonoClock.Instance.NowNs).Words();

    public void Dispose()
    {
        _flash.Dispose();
        _dark.Dispose();
        _presenter.Dispose();
        _platform.Dispose();
    }
}
