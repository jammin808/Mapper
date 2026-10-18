using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Presentation;
using PlayMap.Core.Time;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Platform.Windows;

/// <summary>One display output on the flip model: a borderless top-most popup over the monitor, a waitable FLIP_DISCARD swap chain
/// on it (three buffers, B8G8R8A8, no scaling, no tearing, frame latency 1), buffer 0 wrapped once as an ANGLE pbuffer
/// (<see cref="BackBufferCache{T}"/>), and its frame statistics fed to the output's clock and ledger.
///
/// <list type="bullet">
/// <item><b>Present.</b> The picture is drawn into buffer 0 with the window's surface current, the renderer's surfaceless context made
/// current again, and <c>Present1(1, DO_NOT_WAIT)</c> queues it: a full queue holds the previous picture one refresh (never a block,
/// never a tear); occluded and device-lost come back as the result's problem.</item>
/// <item><b>Pacing.</b> The pacing output (swap interval 1) waits on its waitable object after it presents — pumping window messages
/// while it waits, at most three periods — so the next frame starts just after the vblank, as a GL swap returns. Any other output
/// presents only when its waitable is already signalled (a 0 ms look), else it holds.</item>
/// <item><b>Statistics.</b> After every output presented, <see cref="Observe"/> reads <c>GetFrameStatistics</c> once; a window whose
/// statistics stop updating asks the compositor nudge (DwmFlush on its own thread, never here).</item>
/// </list>
/// Render thread only; nothing here allocates per frame once the back buffer is wrapped.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class DxgiOutputWindow : IOutputPresenter
{
    private const string ClassName = "PlayMapOutput";
    private static readonly ConcurrentDictionary<nint, bool> Closing = new();
    private static readonly Lock ClassGate = new();
    private static bool _registered;

    private readonly D3D11Adapter _adapter;
    private readonly AngleContext _angle;
    private readonly RenderContext _context;
    private readonly DwmNudge _nudge;
    private readonly BackBufferCache<Pbuffer> _backBuffer = new();
    private readonly double _periodMs;
    private nint _hwnd;
    private nint _swapChain;
    private nint _waitable;
    private uint _vao;
    private bool _waited;
    private bool _owed;
    private readonly Func<nint, Pbuffer> _wrap;
    private uint _lastStatsCount;
    private long _lastStatsNs;
    private Core.Media.GpuDeviceLock? _deviceLock;
    private bool _disposed;

    private DxgiOutputWindow(D3D11Adapter adapter, AngleContext angle, RenderContext context, DwmNudge nudge, int width, int height, double hz)
    {
        _adapter = adapter;
        _angle = angle;
        _context = context;
        _nudge = nudge;
        Width = width;
        Height = height;
        // Made once: a method group handed on every frame would be a new delegate every frame.
        _wrap = Wrap;
        _periodMs = 1000.0 / (hz > 1 ? hz : 60);
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsClosing => _hwnd != 0 && Closing.ContainsKey(_hwnd);

    public PresentPath Path => PresentPath.DxgiFlip;

    public int SwapInterval { get; private set; }

    public long LastSwapNs { get; private set; }

    /// <summary>Frame statistics reads that answered nothing new (WARP, a composed window between compositions).</summary>
    public long StatsMisses { get; private set; }

    /// <summary>The swap chain (IDXGISwapChain2*), for the framelock tier's present.</summary>
    public nint SwapChain => _swapChain;

    /// <summary>How many times buffer 0 was wrapped (one per swap chain, again after a resize or a device loss).</summary>
    public int BackBufferWraps => _backBuffer.Wraps;

    public static DxgiOutputWindow Create(D3D11Adapter adapter, AngleContext angle, RenderContext context, OutputPresenterOptions options, DwmNudge nudge,
        Core.Media.GpuDeviceLock deviceLock)
    {
        var (x, y, w, h) = (options.X, options.Y, options.Width, options.Height);
        if (options.Monitor is { } m && options.Borderless)
            (x, y, w, h) = (m.X, m.Y, Math.Max(1, m.Width), Math.Max(1, m.Height));
        else if (options.Monitor is { } placed)
            (x, y) = (placed.X + options.X, placed.Y + options.Y);
        var window = new DxgiOutputWindow(adapter, angle, context, nudge, Math.Max(1, w), Math.Max(1, h), options.Monitor?.RefreshHz ?? 60)
        {
            _deviceLock = deviceLock,
        };
        try
        {
            window.Open(options, x, y);
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    private void Open(OutputPresenterOptions options, int x, int y)
    {
        RegisterClass();
        var style = DxgiNative.WsPopup | DxgiNative.WsVisible;
        var ex = DxgiNative.WsExToolWindow | DxgiNative.WsExNoActivate | (options.Borderless ? DxgiNative.WsExTopMost : 0);
        _hwnd = DxgiNative.CreateWindowEx(ex, ClassName, options.Title, style, x, y, Width, Height, 0, 0, DxgiNative.GetModuleHandle(0), 0);
        if (_hwnd == 0)
            throw new InvalidOperationException($"Windows made no window for {options.Title} (error {Marshal.GetLastPInvokeError()})");
        var on = 1;
        _ = DxgiNative.DwmSetWindowAttribute(_hwnd, DxgiNative.DwmwaTransitionsForceDisabled, &on, sizeof(int));
        var desc = new DxgiSwapChainDesc1((uint)Width, (uint)Height, DxgiNative.FormatB8G8R8A8, 0, 1, 0, DxgiNative.UsageRenderTargetOutput, 3,
            DxgiNative.ScalingNone, DxgiNative.SwapEffectFlipDiscard, DxgiNative.AlphaModeIgnore, DxgiNative.FlagFrameLatencyWaitable);
        nint sc1 = 0;
        var factory = _adapter.Factory;
        var hr = ((delegate* unmanaged<nint, nint, nint, DxgiSwapChainDesc1*, nint, nint, nint*, int>)Com.Slot(factory, ComSlots.FactoryCreateSwapChainForHwnd))(
            factory, _adapter.Device, _hwnd, &desc, 0, 0, &sc1);
        if (hr < 0)
            throw new InvalidOperationException($"DXGI made no flip-model swap chain for {options.Title} ({Com.Hr(hr)})");
        _swapChain = Com.Query(sc1, DxgiNative.IidSwapChain2);
        Com.Release(ref sc1);
        if (_swapChain == 0)
            throw new InvalidOperationException("this Windows has no IDXGISwapChain2 (waitable swap chains need Windows 8.1 or later)");
        _ = ((delegate* unmanaged<nint, nint, uint, int>)Com.Slot(factory, ComSlots.FactoryMakeWindowAssociation))(factory, _hwnd,
            DxgiNative.MwaNoWindowChanges | DxgiNative.MwaNoAltEnter);
        _ = ((delegate* unmanaged<nint, uint, int>)Com.Slot(_swapChain, ComSlots.SwapChainSetMaximumFrameLatency))(_swapChain, 1);
        _waitable = ((delegate* unmanaged<nint, nint>)Com.Slot(_swapChain, ComSlots.SwapChainGetFrameLatencyWaitableObject))(_swapChain);
        if (options.Monitor is { } monitor && ContainingOutput() is { Length: > 0 } containing
            && !string.Equals(containing, monitor.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{options.Title}'s swap chain sits on {containing}, not on {monitor.Name}: move the window's display to the GPU this node renders on");
        _vao = _context.Device.CreateContextVertexArray();
        SwapInterval = options.VSync ? 1 : 0;
    }

    /// <summary>The GDI name of the output the swap chain sits on ("" when DXGI names none, as on WARP).</summary>
    private string ContainingOutput()
    {
        nint output = 0;
        if (((delegate* unmanaged<nint, nint*, int>)Com.Slot(_swapChain, ComSlots.SwapChainGetContainingOutput))(_swapChain, &output) < 0 || output == 0)
            return "";
        DxgiOutputDesc desc = default;
        var ok = ((delegate* unmanaged<nint, DxgiOutputDesc*, int>)Com.Slot(output, ComSlots.OutputGetDesc))(output, &desc) >= 0;
        Com.Release(ref output);
        return ok ? DxgiNative.Text(&desc, 0, DxgiOutputDesc.DeviceNameChars) : "";
    }

    private static void RegisterClass()
    {
        lock (ClassGate)
        {
            if (_registered)
                return;
            var name = Marshal.StringToHGlobalUni(ClassName);
            var wc = new WndClassEx((uint)sizeof(WndClassEx), DxgiNative.CsOwnDc, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                DxgiNative.GetModuleHandle(0), 0, name);
            // The class name stays allocated for the process: Windows reads it for every window of the class.
            if (DxgiNative.RegisterClassEx(&wc) == 0)
                throw new InvalidOperationException($"Windows registered no output window class (error {Marshal.GetLastPInvokeError()})");
            _registered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case DxgiNative.WmEraseBkgnd:
                return 1;
            case DxgiNative.WmSetCursor:
                _ = DxgiNative.SetCursor(0);
                return 1;
            case DxgiNative.WmClose:
                Closing[hwnd] = true;
                return 0;
            default:
                return DxgiNative.DefWindowProc(hwnd, msg, w, l);
        }
    }

    /// <summary>Dispatches every queued window message of this thread; never blocks.</summary>
    public static void PumpMessages()
    {
        WinMsg msg = default;
        while (DxgiNative.PeekMessage(&msg, 0, 0, 0, DxgiNative.PmRemove))
        {
            _ = DxgiNative.TranslateMessage(&msg);
            _ = DxgiNative.DispatchMessage(&msg);
        }
    }

    public void SetSwapInterval(int interval) => SwapInterval = interval;

    /// <summary>Waits until the swap chain takes a new frame, pumping messages, at most three periods (a stall is the air's, never
    /// the thread's): true when it signalled. The device's lock is let go of for the wait (decoders on the device submit while the
    /// frame waits for the vertical blank) and taken back within its bound after.</summary>
    private bool WaitForFrame()
    {
        var holds = _deviceLock?.ExitAll() ?? 0;
        try
        {
            var deadline = MonoClock.Instance.NowNs + (long)(3 * _periodMs * 1e6);
            var handle = _waitable;
            while (true)
            {
                var left = (deadline - MonoClock.Instance.NowNs) / 1_000_000;
                if (left < 0)
                    return false;
                var r = DxgiNative.MsgWaitForMultipleObjectsEx(1, &handle, (uint)left, DxgiNative.QsAllInput, DxgiNative.MwmoInputAvailable);
                if (r == DxgiNative.WaitObject0)
                    return true;
                if (r != DxgiNative.WaitObject0 + 1)
                    return false;
                PumpMessages();
            }
        }
        finally
        {
            _ = _deviceLock?.Reenter(holds, Core.Media.GpuDeviceLock.FrameBoundMs);
        }
    }

    public PresentResult Present(GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return PresentDrawn(picture);
    }

    public PresentResult PresentBlack() => PresentDrawn(null);

    private PresentResult PresentDrawn(GpuTexture? picture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pacer = SwapInterval == 1;
        if (pacer && !_waited)
        {
            // The first frame waits for the swap chain before it (a frame drawn before the queue has room starts a frame late).
            _ = WaitForFrame();
            _waited = true;
        }
        else if (!pacer && !_owed && DxgiNative.WaitForSingleObjectEx(_waitable, 0, 0) != DxgiNative.WaitObject0)
        {
            return new PresentResult(false, MonoClock.Instance.NowNs, PresentProblem.Held);
        }
        // The waitable's count taken for this present is spent only by a present that queues: one that does not leaves it owed, and
        // the next present uses it instead of waiting again (a count lost would hold the output forever at frame latency 1).
        _owed = !pacer;
        Draw(picture);
        var parameters = default(DxgiPresentParameters);
        var hr = ((delegate* unmanaged<nint, uint, uint, DxgiPresentParameters*, int>)Com.Slot(_swapChain, ComSlots.SwapChainPresent1))(
            _swapChain, 1, DxgiNative.PresentDoNotWait, &parameters);
        var problem = hr switch
        {
            DxgiNative.SOk => PresentProblem.None,
            DxgiNative.ErrorWasStillDrawing => PresentProblem.Held,
            DxgiNative.StatusOccluded => PresentProblem.Occluded,
            DxgiNative.ErrorDeviceRemoved or DxgiNative.ErrorDeviceReset => PresentProblem.DeviceLost,
            _ => hr < 0 ? PresentProblem.Failed : PresentProblem.None,
        };
        if (problem != PresentProblem.None)
            return new PresentResult(false, MonoClock.Instance.NowNs, problem);
        _owed = false;
        uint count = 0;
        _ = ((delegate* unmanaged<nint, uint*, int>)Com.Slot(_swapChain, ComSlots.SwapChainGetLastPresentCount))(_swapChain, &count);
        if (pacer)
            _ = WaitForFrame();
        LastSwapNs = MonoClock.Instance.NowNs;
        return PresentResult.Done(LastSwapNs) with { PresentCount = count };
    }

    /// <summary>Draws the picture (black for null) into buffer 0 with the window's surface current — rows as they are: ANGLE's surface
    /// on a Direct3D 11 texture runs top first, as PlayMap's targets do (the WARP read-back proves it) — then makes the renderer's
    /// surfaceless context current again. One immediate context serves ANGLE and DXGI on this thread, so a flush orders the draw
    /// before the flip with no fence.</summary>
    private void Draw(GpuTexture? picture)
    {
        var pb = BackBuffer();
        var egl = _angle.Egl;
        if (egl.MakeCurrent(_angle.Display, pb.Surface, pb.Surface, _angle.Context) == 0)
            throw new InvalidOperationException($"buffer 0's surface could not be made current ({egl.ErrorWords()})");
        var device = _context.Device;
        try
        {
            if (picture is null)
                device.ClearDefault(Width, Height);
            else
                device.DrawPresent(_vao, picture, Width, Height, flipY: false);
            device.Flush();
        }
        finally
        {
            device.InvalidateState();
            _angle.MakeCurrent();
        }
    }

    private Pbuffer BackBuffer()
    {
        nint texture = 0;
        var iid = DxgiNative.IidTexture2D;
        var hr = ((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Com.Slot(_swapChain, ComSlots.SwapChainGetBuffer))(_swapChain, 0, &iid, &texture);
        if (hr < 0)
            throw new InvalidOperationException($"the swap chain gave no buffer 0 ({Com.Hr(hr)})");
        try
        {
            return _backBuffer.Get(texture, _wrap);
        }
        finally
        {
            Com.Release(ref texture);
        }
    }

    private Pbuffer Wrap(nint texture)
    {
        var egl = _angle.Egl;
        int* attribs = stackalloc int[] { AngleEgl.Width, Width, AngleEgl.Height, Height, AngleEgl.None };
        var surface = egl.CreatePbufferFromClientBuffer(_angle.Display, AngleEgl.D3DTextureAngle, texture, _angle.Config, attribs);
        if (surface == 0)
            throw new InvalidOperationException($"ANGLE could not wrap buffer 0 as a surface ({egl.ErrorWords()})");
        return new Pbuffer(_angle, surface);
    }

    /// <summary>After every output presented: the display's statistics into the clock and ledger (the present the display showed
    /// last, the refresh it first showed at, the newest vblank and its instant). Statistics that stopped moving for two periods while
    /// presents are accepted ask the compositor nudge; the clock then times swap returns, with no display evidence.</summary>
    public void Observe(PresentClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DxgiFrameStatistics stats = default;
        var hr = ((delegate* unmanaged<nint, DxgiFrameStatistics*, int>)Com.Slot(_swapChain, ComSlots.SwapChainGetFrameStatistics))(_swapChain, &stats);
        var now = MonoClock.Instance.NowNs;
        if (hr == DxgiNative.ErrorFrameStatisticsDisjoint)
        {
            clock.OnDisplayDisjoint();
            return;
        }
        if (hr < 0 || stats.PresentCount == _lastStatsCount)
        {
            StatsMisses++;
            if (_lastStatsNs != 0 && now - _lastStatsNs > (long)(2 * _periodMs * 1e6))
            {
                _nudge.Ask();
                clock.OnDisplayStale();
            }
            return;
        }
        _lastStatsCount = stats.PresentCount;
        _lastStatsNs = now;
        clock.OnDisplayStatistics(stats.PresentCount, stats.PresentRefreshCount, stats.SyncRefreshCount,
            QpcTime.ToNs(stats.SyncQpcTime, Stopwatch.Frequency));
    }

    /// <summary>Tests: draws a picture into buffer 0 and reads it back through a staging copy (top row first, BGRA), without
    /// presenting; blocks on the map.</summary>
    internal bool ReadBack(GpuTexture picture, Span<byte> bgra)
    {
        Draw(picture);
        nint texture = 0;
        var iid = DxgiNative.IidTexture2D;
        if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Com.Slot(_swapChain, ComSlots.SwapChainGetBuffer))(_swapChain, 0, &iid, &texture) < 0)
            return false;
        try
        {
            return D3D11Readback.Read(_adapter.Device, _adapter.Context, texture, Width, Height, bgra);
        }
        finally
        {
            Com.Release(ref texture);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _backBuffer.Dispose();
        if (_vao != 0)
            _context.Device.DeleteContextVertexArray(_vao);
        _vao = 0;
        if (_waitable != 0)
            _ = DxgiNative.CloseHandle(_waitable);
        _waitable = 0;
        Com.Release(ref _swapChain);
        if (_hwnd != 0)
        {
            Closing.TryRemove(_hwnd, out _);
            _ = DxgiNative.DestroyWindow(_hwnd);
        }
        _hwnd = 0;
    }

    /// <summary>buffer 0 as ANGLE's pbuffer surface.</summary>
    private sealed class Pbuffer(AngleContext angle, nint surface) : IDisposable
    {
        public nint Surface { get; private set; } = surface;

        public void Dispose()
        {
            if (Surface == 0)
                return;
            _ = angle.Egl.DestroySurface(angle.Display, Surface);
            Surface = 0;
        }
    }
}

/// <summary>Reads a Direct3D 11 texture back through a staging copy (tests and snapshots only: the map waits for the GPU).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class D3D11Readback
{
    private const uint UsageStaging = 3;
    private const uint CpuAccessRead = 0x20000;
    private const uint MapRead = 1;

    public static bool Read(nint device, nint context, nint texture, int width, int height, Span<byte> bgra)
    {
        var desc = new D3D11Texture2DDesc((uint)width, (uint)height, 1, 1, DxgiNative.FormatB8G8R8A8, 1, 0, UsageStaging, 0, CpuAccessRead, 0);
        nint staging = 0;
        if (((delegate* unmanaged<nint, D3D11Texture2DDesc*, nint, nint*, int>)Com.Slot(device, ComSlots.DeviceCreateTexture2D))(device, &desc, 0, &staging) < 0)
            return false;
        try
        {
            ((delegate* unmanaged<nint, nint, nint, void>)Com.Slot(context, ComSlots.ContextCopyResource))(context, staging, texture);
            D3D11MappedSubresource mapped = default;
            if (((delegate* unmanaged<nint, nint, uint, uint, uint, D3D11MappedSubresource*, int>)Com.Slot(context, ComSlots.ContextMap))(context, staging, 0, MapRead, 0, &mapped) < 0)
                return false;
            var row = width * 4;
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<byte>((byte*)mapped.Data + (long)y * mapped.RowPitch, row).CopyTo(bgra.Slice(y * row, row));
            ((delegate* unmanaged<nint, nint, uint, void>)Com.Slot(context, ComSlots.ContextUnmap))(context, staging, 0);
            return true;
        }
        finally
        {
            Com.Release(ref staging);
        }
    }
}
