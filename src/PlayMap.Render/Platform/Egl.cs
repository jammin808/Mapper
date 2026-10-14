using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Platform;

/// <summary>The few EGL 1.5 entry points a context with no window needs (Mesa's surfaceless platform): a display, a config, a
/// context made current with no surface, and the proc loader. Linux only; libEGL.so.1 is loaded from the system.</summary>
[SupportedOSPlatform("linux")]
internal static partial class Egl
{
    private const string Lib = "libEGL.so.1";

    public const int Success = 0x3000;
    public const int None = 0x3038;
    public const int Vendor = 0x3053;
    public const int Version = 0x3054;
    public const int Extensions = 0x3055;
    public const int RenderableType = 0x3040;
    public const int SurfaceType = 0x3033;
    public const int PbufferBit = 0x0001;
    public const int OpenGlEs3Bit = 0x0040;
    public const int OpenGlBit = 0x0008;
    public const int RedSize = 0x3024;
    public const int GreenSize = 0x3023;
    public const int BlueSize = 0x3022;
    public const int OpenGlEsApi = 0x30A0;
    public const int OpenGlApi = 0x30A2;
    public const int ContextMajorVersion = 0x3098;
    public const int ContextMinorVersion = 0x30FB;
    public const int ContextOpenGlProfileMask = 0x30FD;
    public const int ContextOpenGlCoreProfileBit = 0x0001;
    public const int ContextOpenGlForwardCompatible = 0x31B1;
    public const int ContextOpenGlDebug = 0x31B0;
    public const int PlatformSurfacelessMesa = 0x31DD;

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(string name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetPlatformDisplay")]
    public static partial nint GetPlatformDisplay(int platform, nint nativeDisplay, nint attribList);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglInitialize")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint Initialize(nint display, out int major, out int minor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglTerminate")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint Terminate(nint display);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglBindAPI")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint BindApi(int api);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglChooseConfig")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint ChooseConfig(nint display, int[] attribList, nint[] configs, int configSize, out int numConfig);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglCreateContext")]
    public static partial nint CreateContext(nint display, nint config, nint shareContext, int[] attribList);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglDestroyContext")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint DestroyContext(nint display, nint context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglMakeCurrent")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint MakeCurrent(nint display, nint draw, nint read, nint context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglReleaseThread")]
    [return: MarshalAs(UnmanagedType.U4)]
    public static partial uint ReleaseThread();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetError")]
    public static partial int GetError();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglQueryString")]
    public static partial nint QueryString(nint display, int name);

    public static string ErrorWords() => string.Create(CultureInfo.InvariantCulture, $"EGL error 0x{GetError():X4}");

    public static string Query(nint display, int name) => Marshal.PtrToStringUTF8(QueryString(display, name)) ?? "";
}

/// <summary>An EGL context with no window and no surface (Mesa's surfaceless platform): every pass draws into its own
/// framebuffers, so a headless renderer and the ES test leg need nothing else. Made, used and disposed on one thread.
///
/// The surfaceless display is one per process (every eglGetPlatformDisplay call answers the same handle) and eglTerminate is not
/// counted, so the display is initialised by the first live context and terminated only when the last one is disposed: one
/// context going never pulls the display from under another on another thread.</summary>
[SupportedOSPlatform("linux")]
internal sealed class EglSurfacelessContext : IDisposable
{
    private static readonly Lock DisplayGate = new();
    private static int _displayUsers;

    private nint _display;
    private nint _context;

    private EglSurfacelessContext(nint display, nint context, string version)
    {
        _display = display;
        _context = context;
        EglVersion = version;
    }

    /// <summary>"1.5 Mesa …": what the EGL library said it is.</summary>
    public string EglVersion { get; }

    /// <summary>Makes a context of the dialect (ES 3.0, or desktop 3.3 core) current on this thread; throws with words when the
    /// library, the surfaceless platform or the context is not there.</summary>
    public static EglSurfacelessContext Create(GlDialect dialect, bool debug)
    {
        var (display, major, minor) = AcquireDisplay();
        try
        {
            var es = dialect == GlDialect.Es300;
            if (Egl.BindApi(es ? Egl.OpenGlEsApi : Egl.OpenGlApi) == 0)
                throw new PlatformNotSupportedException($"EGL binds no {(es ? "OpenGL ES" : "OpenGL")} here ({Egl.ErrorWords()})");
            int[] configAttribs =
            [
                Egl.RenderableType, es ? Egl.OpenGlEs3Bit : Egl.OpenGlBit,
                Egl.SurfaceType, Egl.PbufferBit,
                Egl.RedSize, 8, Egl.GreenSize, 8, Egl.BlueSize, 8,
                Egl.None,
            ];
            var configs = new nint[1];
            if (Egl.ChooseConfig(display, configAttribs, configs, 1, out var count) == 0 || count < 1)
                throw new PlatformNotSupportedException($"EGL offers no {(es ? "ES 3" : "OpenGL")} config here ({Egl.ErrorWords()})");
            var contextAttribs = new List<int> { Egl.ContextMajorVersion, 3, Egl.ContextMinorVersion, es ? 0 : 3 };
            if (!es)
                contextAttribs.AddRange([Egl.ContextOpenGlProfileMask, Egl.ContextOpenGlCoreProfileBit, Egl.ContextOpenGlForwardCompatible, 1]);
            if (debug)
                contextAttribs.AddRange([Egl.ContextOpenGlDebug, 1]);
            contextAttribs.Add(Egl.None);
            var context = Egl.CreateContext(display, configs[0], 0, [.. contextAttribs]);
            if (context == 0)
                throw new PlatformNotSupportedException($"EGL made no {(es ? "ES 3.0" : "3.3 core")} context ({Egl.ErrorWords()})");
            if (Egl.MakeCurrent(display, 0, 0, context) == 0)
            {
                _ = Egl.DestroyContext(display, context);
                throw new PlatformNotSupportedException($"EGL could not make a context current with no surface ({Egl.ErrorWords()})");
            }
            return new EglSurfacelessContext(display, context,
                string.Create(CultureInfo.InvariantCulture, $"{major}.{minor} {Egl.Query(display, Egl.Vendor)}"));
        }
        catch
        {
            ReleaseDisplay(display);
            throw;
        }
    }

    /// <summary>The process's surfaceless display, initialised (once, for its first user); its version.</summary>
    private static (nint Display, int Major, int Minor) AcquireDisplay()
    {
        lock (DisplayGate)
        {
            nint display;
            try
            {
                display = Egl.GetPlatformDisplay(Egl.PlatformSurfacelessMesa, 0, 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException($"EGL is not on this machine ({ex.Message})", ex);
            }
            if (display == 0)
                throw new PlatformNotSupportedException($"EGL has no surfaceless platform here ({Egl.ErrorWords()})");
            // Initialising an initialised display only answers its version.
            if (Egl.Initialize(display, out var major, out var minor) == 0)
                throw new PlatformNotSupportedException($"EGL did not initialise ({Egl.ErrorWords()})");
            _displayUsers++;
            return (display, major, minor);
        }
    }

    /// <summary>One user less; the last one terminates the display.</summary>
    private static void ReleaseDisplay(nint display)
    {
        lock (DisplayGate)
        {
            if (--_displayUsers == 0)
                _ = Egl.Terminate(display);
        }
    }

    /// <summary>The GL loader for this context: eglGetProcAddress (which answers core and extension names alike in EGL 1.5).</summary>
    public static nint ProcAddress(string name) => Egl.GetProcAddress(name);

    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
        if (Egl.MakeCurrent(_display, 0, 0, _context) == 0)
            throw new InvalidOperationException($"the EGL context could not be made current ({Egl.ErrorWords()})");
    }

    public void Dispose()
    {
        if (_context == 0)
            return;
        // Teardown: each call's failure leaves nothing more to undo, so the results are not acted on.
        _ = Egl.MakeCurrent(_display, 0, 0, 0);
        _ = Egl.DestroyContext(_display, _context);
        _ = Egl.ReleaseThread();
        ReleaseDisplay(_display);
        _context = 0;
        _display = 0;
    }
}
