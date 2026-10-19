namespace PlayMap.Render.Gl;

/// <summary>The stages the DXGI flip platform comes up through, in order; a start that stops names the stage it stopped at.</summary>
public enum DxgiStartStage
{
    /// <summary>The DXGI factory and the adapter that drives the first display output.</summary>
    Adapter,
    /// <summary>The Direct3D 11 device on it.</summary>
    Device,
    /// <summary>ANGLE's library loaded by name and its EGL entry points resolved.</summary>
    Angle,
    /// <summary>ANGLE's client, device and display extensions present.</summary>
    Extensions,
    /// <summary>The EGL display on our device, its ES 3.0 context made current.</summary>
    Context,
    /// <summary>The GL device on that context and every shader program translated.</summary>
    Programs,
}

/// <summary>A platform start that stopped at a stage, with the words why.</summary>
public sealed class GpuPlatformStartException : Exception
{
    public GpuPlatformStartException(DxgiStartStage stage, string words)
        : base(words)
    {
        Stage = stage;
    }

    public GpuPlatformStartException()
    {
    }

    public GpuPlatformStartException(string message)
        : base(message)
    {
    }

    public GpuPlatformStartException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public DxgiStartStage Stage { get; }

    public static string Word(DxgiStartStage stage) => stage switch
    {
        DxgiStartStage.Adapter => "adapter",
        DxgiStartStage.Device => "device",
        DxgiStartStage.Angle => "angle",
        DxgiStartStage.Extensions => "extensions",
        DxgiStartStage.Context => "context",
        _ => "programs",
    };
}

/// <summary>What the flip path needs of ANGLE, in the three lists EGL reports them in (client extensions before any display, the
/// device's, the display's), and the library it loads: the single name <c>av_libglesv2</c>, the file Avalonia.Angle.Windows.Natives
/// puts beside the exe (its EGL entry points are in the same library). Pure: the refusal names the first missing extension and
/// the list it is missing from.</summary>
public static class AngleRequirements
{
    /// <summary>The library name the loader asks for (NativeLibrary adds ".dll" on Windows).</summary>
    public const string Library = "av_libglesv2";

    /// <summary>The files the loader may find: the library for each Windows runtime the package ships.</summary>
    public static readonly IReadOnlyList<string> Files =
        ["runtimes/win-x64/native/av_libglesv2.dll", "runtimes/win-x86/native/av_libglesv2.dll", "runtimes/win-arm64/native/av_libglesv2.dll"];

    /// <summary>The EGL entry points the loader resolves from the library, by their EGL names.</summary>
    public static readonly IReadOnlyList<string> Entries =
    [
        "eglGetProcAddress", "eglQueryString", "eglGetError", "eglInitialize", "eglTerminate", "eglBindAPI", "eglChooseConfig",
        "eglCreateContext", "eglDestroyContext", "eglMakeCurrent", "eglCreatePbufferFromClientBuffer", "eglDestroySurface", "eglReleaseThread",
        "eglCreateDeviceANGLE", "eglReleaseDeviceANGLE", "eglGetPlatformDisplayEXT", "eglQueryDeviceStringEXT", "eglQueryDisplayAttribEXT",
        "eglQueryDeviceAttribEXT",
    ];

    /// <summary>The name libGLESv2 exports an EGL entry point under: <c>eglFoo</c> → <c>EGL_Foo</c> (ANGLE's libEGL forwards to
    /// these; the single library has no <c>egl</c>-prefixed exports).</summary>
    public static string ExportName(string eglName)
    {
        ArgumentNullException.ThrowIfNull(eglName);
        return eglName.StartsWith("egl", StringComparison.Ordinal) ? "EGL_" + eglName[3..] : eglName;
    }

    public static readonly IReadOnlyList<string> Client =
        ["EGL_EXT_platform_base", "EGL_EXT_platform_device", "EGL_EXT_device_query", "EGL_ANGLE_device_creation", "EGL_ANGLE_device_creation_d3d11"];

    public static readonly IReadOnlyList<string> Device = ["EGL_ANGLE_device_d3d11"];

    public static readonly IReadOnlyList<string> Display = ["EGL_ANGLE_d3d_texture_client_buffer", "EGL_KHR_surfaceless_context"];

    /// <summary>The first extension of <paramref name="needed"/> the space-separated <paramref name="offered"/> lacks, as the refusal
    /// says it ("ANGLE's display lacks EGL_KHR_surfaceless_context"); null when all are there.</summary>
    public static string? Missing(string list, IReadOnlyList<string> needed, string? offered)
    {
        ArgumentNullException.ThrowIfNull(needed);
        var have = (offered ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var n in needed)
            if (Array.IndexOf(have, n) < 0)
                return $"ANGLE's {list} lacks {n}";
        return null;
    }

    /// <summary>Whether the display offers route A (an EGLImage of a D3D11 texture: no context switch per output).</summary>
    public static bool HasImageRoute(string? displayExtensions) =>
        (displayExtensions ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("EGL_ANGLE_image_d3d11_texture", StringComparer.Ordinal);
}

/// <summary>QPC ticks to nanoseconds without overflow: <c>(qpc / f) · 10⁹ + (qpc % f) · 10⁹ / f</c> — the plain product overflows Int64
/// after a day of uptime at 10 MHz.</summary>
public static class QpcTime
{
    public static long ToNs(long qpc, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return qpc / frequency * 1_000_000_000L + qpc % frequency * 1_000_000_000L / frequency;
    }
}

/// <summary>The one wrapper of a swap chain's buffer 0: in Direct3D 11's flip model the runtime renames buffer 0's storage on each
/// present, so the texture <c>GetBuffer(0)</c> answers is the same object and one wrapper made on it stays valid until the buffers are
/// resized or the device is lost. The cache holds exactly one entry; asked for a different texture while one is held (a driver or
/// OS that rotates buffers after all), it says so rather than wrapping silently. Render thread.</summary>
public sealed class BackBufferCache<T> : IDisposable
    where T : class, IDisposable
{
    private nint _texture;
    private T? _wrapped;

    public int Count => _wrapped is null ? 0 : 1;

    /// <summary>Wraps made since the swap chain opened (one, plus one per resize or device loss).</summary>
    public int Wraps { get; private set; }

    /// <summary>The wrapper of <paramref name="texture"/>, made by <paramref name="wrap"/> the first time; throws when a different
    /// texture is asked for while one is held (call <see cref="Invalidate"/> after ResizeBuffers or a device loss).</summary>
    public T Get(nint texture, Func<nint, T> wrap)
    {
        ArgumentNullException.ThrowIfNull(wrap);
        if (_wrapped is not null)
        {
            if (texture != _texture)
                throw new InvalidOperationException("buffer 0 answered a different texture without a resize: the flip model renamed no storage");
            return _wrapped;
        }
        _wrapped = wrap(texture);
        _texture = texture;
        Wraps++;
        return _wrapped;
    }

    /// <summary>Lets the wrapper go (ResizeBuffers or a device loss): the next <see cref="Get"/> wraps again.</summary>
    public void Invalidate()
    {
        _wrapped?.Dispose();
        _wrapped = null;
        _texture = 0;
    }

    public void Dispose() => Invalidate();
}
