using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Render.Platform.Windows;

/// <summary>ANGLE (BSD) loaded at run time by the single name <see cref="AngleRequirements.Library"/> — the file
/// Avalonia.Angle.Windows.Natives puts beside the exe; no binary is shipped by PlayMap — and the EGL entry points the flip path calls,
/// resolved from that library's handle by the names libGLESv2 exports them under (<c>eglFoo</c> as <c>EGL_Foo</c>,
/// <see cref="AngleRequirements.ExportName"/>; GL's own through EGL_GetProcAddress). Holds the library for its life.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AngleEgl : IDisposable
{
    public const int None = 0x3038;
    public const int Extensions = 0x3055;
    public const int Width = 0x3057;
    public const int Height = 0x3056;
    public const int SurfaceType = 0x3033;
    public const int PbufferBit = 0x0001;
    public const int RenderableType = 0x3040;
    public const int OpenGlEs3Bit = 0x0040;
    public const int RedSize = 0x3024;
    public const int GreenSize = 0x3023;
    public const int BlueSize = 0x3022;
    public const int AlphaSize = 0x3021;
    public const int OpenGlEsApi = 0x30A0;
    public const int ContextMajorVersion = 0x3098;
    public const int ContextMinorVersion = 0x30FB;
    public const int PlatformDeviceExt = 0x313F;
    public const int DeviceExt = 0x322C;
    public const int D3D11DeviceAngle = 0x33A1;
    public const int D3DTextureAngle = 0x33A3;

    private nint _library;

    private AngleEgl(nint library) => _library = library;

    public delegate* unmanaged<byte*, nint> GetProcAddress { get; private set; }
    public delegate* unmanaged<nint, int, byte*> QueryString { get; private set; }
    public delegate* unmanaged<int> GetError { get; private set; }
    public delegate* unmanaged<nint, int*, int*, uint> Initialize { get; private set; }
    public delegate* unmanaged<nint, uint> Terminate { get; private set; }
    public delegate* unmanaged<int, uint> BindApi { get; private set; }
    public delegate* unmanaged<nint, int*, nint*, int, int*, uint> ChooseConfig { get; private set; }
    public delegate* unmanaged<nint, nint, nint, int*, nint> CreateContext { get; private set; }
    public delegate* unmanaged<nint, nint, uint> DestroyContext { get; private set; }
    public delegate* unmanaged<nint, nint, nint, nint, uint> MakeCurrent { get; private set; }
    public delegate* unmanaged<nint, int, nint, nint, int*, nint> CreatePbufferFromClientBuffer { get; private set; }
    public delegate* unmanaged<nint, nint, uint> DestroySurface { get; private set; }
    public delegate* unmanaged<uint> ReleaseThread { get; private set; }

    // Extension entry points (EGL_EXT_platform_base, EGL_EXT_device_query, EGL_ANGLE_device_creation).
    public delegate* unmanaged<int, nint, nint*, nint> CreateDeviceAngle { get; private set; }
    public delegate* unmanaged<nint, uint> ReleaseDeviceAngle { get; private set; }
    public delegate* unmanaged<int, nint, int*, nint> GetPlatformDisplayExt { get; private set; }
    public delegate* unmanaged<nint, int, byte*> QueryDeviceStringExt { get; private set; }
    public delegate* unmanaged<nint, int, nint*, uint> QueryDisplayAttribExt { get; private set; }
    public delegate* unmanaged<nint, int, nint*, uint> QueryDeviceAttribExt { get; private set; }

    /// <summary>Loads the library beside the exe (or the assembly) and resolves every entry point; throws at stage Angle with words
    /// naming what is missing.</summary>
    public static AngleEgl Load()
    {
        if (!NativeLibrary.TryLoad(AngleRequirements.Library, typeof(AngleEgl).Assembly,
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.ApplicationDirectory, out var library))
            throw new GpuPlatformStartException(DxgiStartStage.Angle,
                $"ANGLE is not beside the exe ({AngleRequirements.Library}.dll from Avalonia.Angle.Windows.Natives)");
        var egl = new AngleEgl(library);
        try
        {
            egl.Resolve();
            return egl;
        }
        catch
        {
            egl.Dispose();
            throw;
        }
    }

    private void Resolve()
    {
        GetProcAddress = (delegate* unmanaged<byte*, nint>)Export("eglGetProcAddress");
        QueryString = (delegate* unmanaged<nint, int, byte*>)Export("eglQueryString");
        GetError = (delegate* unmanaged<int>)Export("eglGetError");
        Initialize = (delegate* unmanaged<nint, int*, int*, uint>)Export("eglInitialize");
        Terminate = (delegate* unmanaged<nint, uint>)Export("eglTerminate");
        BindApi = (delegate* unmanaged<int, uint>)Export("eglBindAPI");
        ChooseConfig = (delegate* unmanaged<nint, int*, nint*, int, int*, uint>)Export("eglChooseConfig");
        CreateContext = (delegate* unmanaged<nint, nint, nint, int*, nint>)Export("eglCreateContext");
        DestroyContext = (delegate* unmanaged<nint, nint, uint>)Export("eglDestroyContext");
        MakeCurrent = (delegate* unmanaged<nint, nint, nint, nint, uint>)Export("eglMakeCurrent");
        CreatePbufferFromClientBuffer = (delegate* unmanaged<nint, int, nint, nint, int*, nint>)Export("eglCreatePbufferFromClientBuffer");
        DestroySurface = (delegate* unmanaged<nint, nint, uint>)Export("eglDestroySurface");
        ReleaseThread = (delegate* unmanaged<uint>)Export("eglReleaseThread");
        CreateDeviceAngle = (delegate* unmanaged<int, nint, nint*, nint>)Extension("eglCreateDeviceANGLE");
        ReleaseDeviceAngle = (delegate* unmanaged<nint, uint>)Extension("eglReleaseDeviceANGLE");
        GetPlatformDisplayExt = (delegate* unmanaged<int, nint, int*, nint>)Extension("eglGetPlatformDisplayEXT");
        QueryDeviceStringExt = (delegate* unmanaged<nint, int, byte*>)Extension("eglQueryDeviceStringEXT");
        QueryDisplayAttribExt = (delegate* unmanaged<nint, int, nint*, uint>)Extension("eglQueryDisplayAttribEXT");
        QueryDeviceAttribExt = (delegate* unmanaged<nint, int, nint*, uint>)Extension("eglQueryDeviceAttribEXT");
    }

    private nint Export(string name) =>
        NativeLibrary.TryGetExport(_library, AngleRequirements.ExportName(name), out var p)
            ? p
            : throw new GpuPlatformStartException(DxgiStartStage.Angle,
                $"ANGLE's {AngleRequirements.Library} has no {AngleRequirements.ExportName(name)} ({name})");

    /// <summary>An extension entry point: the library's export where it has one, else through EGL_GetProcAddress (0 when neither).</summary>
    private nint Extension(string name) =>
        NativeLibrary.TryGetExport(_library, AngleRequirements.ExportName(name), out var p) ? p : Proc(name);

    /// <summary>A GL or EGL extension entry point through eglGetProcAddress (0 when ANGLE has none).</summary>
    public nint Proc(string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* b = bytes)
            return GetProcAddress(b);
    }

    public string Query(nint display, int name) => Marshal.PtrToStringAnsi((nint)QueryString(display, name)) ?? "";

    public string DeviceString(nint device, int name) =>
        QueryDeviceStringExt is null ? "" : Marshal.PtrToStringAnsi((nint)QueryDeviceStringExt(device, name)) ?? "";

    public string ErrorWords() => "EGL error 0x" + GetError().ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Lets the entry points go; the library itself stays loaded for the process (ANGLE keeps worker threads of its own,
    /// and the desk's own ANGLE use loads the same file), so nothing is freed.</summary>
    public void Dispose() => _library = 0;
}
