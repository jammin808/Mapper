using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.PM;
using static TerraFX.Interop.Windows.WM;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.Windows.WS;

namespace PlayMap.Bench;

/// <summary>A plain Win32 window for a swap chain: borderless over a rectangle of the desktop (a monitor, for the lab) or a framed
/// one, shown or hidden. Its messages are pumped by the thread that made it; closing it sets <see cref="Closed"/> and nothing else,
/// so a run ends at its own pace.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal sealed unsafe class BenchWindow : IDisposable
{
    private const string ClassName = "PlayMapBench";
    private static bool _registered;
    private static volatile bool _closed;

    public BenchWindow(string title, int x, int y, int width, int height, bool visible, bool borderless)
    {
        var instance = (HINSTANCE)GetModuleHandleW(null);
        Register(instance);
        fixed (char* cls = ClassName)
        fixed (char* caption = title)
        {
            var style = borderless ? WS_POPUP : (uint)WS_OVERLAPPEDWINDOW;
            if (visible)
                style |= WS_VISIBLE;
            Handle = CreateWindowExW(0, cls, caption, style, x, y, width, height, HWND.NULL, HMENU.NULL, instance, null);
        }
        if (Handle == HWND.NULL)
            throw new InvalidOperationException($"CreateWindowExW answered error {Marshal.GetLastSystemError()}");
        Width = width;
        Height = height;
    }

    /// <summary>The window class every bench window shares, registered once per process.</summary>
    private static void Register(HINSTANCE instance)
    {
        if (_registered)
            return;
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &Procedure,
                hInstance = instance,
                lpszClassName = cls,
            };
            if (RegisterClassExW(&wc) == 0)
                throw new InvalidOperationException($"RegisterClassExW answered error {Marshal.GetLastSystemError()}");
        }
        _registered = true;
    }

    public HWND Handle { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>A window of this process was asked to close.</summary>
    public static bool Closed => _closed;

    /// <summary>Handles every message waiting for this thread's windows, never waiting for one.</summary>
    public static void Pump()
    {
        MSG message;
        while (PeekMessageW(&message, HWND.NULL, 0, 0, PM_REMOVE))
        {
            _ = TranslateMessage(&message);
            _ = DispatchMessageW(&message);
        }
    }

    [UnmanagedCallersOnly]
    private static LRESULT Procedure(HWND window, uint message, WPARAM w, LPARAM l)
    {
        if (message == WM_CLOSE)
        {
            _closed = true;
            return default;
        }
        return DefWindowProcW(window, message, w, l);
    }

    public void Dispose() => _ = DestroyWindow(Handle);
}
