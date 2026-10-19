using System.Globalization;
using System.Runtime.InteropServices;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;

namespace PlayMap.Render.Gpu;

/// <summary>The EGL entry points zero-copy imports need, resolved from the system's libEGL at run time: the current display, image
/// creation and destruction (EGL_KHR_image_base), the dmabuf format queries (EGL_EXT_image_dma_buf_import_modifiers) and the device
/// queries (EGL_EXT_device_query, EGL_EXT_device_drm_render_node). Tokens from the Khronos registry's eglext.h and the extension
/// specifications named.</summary>
internal static unsafe partial class EglImages
{
    private const string Lib = "libEGL.so.1";

    public const int None = 0x3038;
    public const int Extensions = 0x3055;
    public const int Width = 0x3057;
    public const int Height = 0x3056;
    // EGL_EXT_image_dma_buf_import.
    public const int LinuxDmaBuf = 0x3270;
    public const int LinuxDrmFourcc = 0x3271;
    public const int Plane0Fd = 0x3272;
    public const int Plane0Offset = 0x3273;
    public const int Plane0Pitch = 0x3274;
    // EGL_EXT_image_dma_buf_import_modifiers.
    public const int Plane0ModifierLo = 0x3443;
    public const int Plane0ModifierHi = 0x3444;
    // EGL_EXT_device_query, EGL_EXT_device_drm_render_node.
    public const int Device = 0x322C;
    public const int DrmRenderNodeFile = 0x3377;
    // EGL_KHR_gl_texture_2D_image.
    public const int GlTexture2D = 0x30B1;

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(string name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetCurrentDisplay")]
    public static partial nint GetCurrentDisplay();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetCurrentContext")]
    public static partial nint GetCurrentContext();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglGetError")]
    public static partial int GetError();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "eglQueryString")]
    public static partial nint QueryString(nint display, int name);

    /// <summary>The current EGL display, or 0 when EGL is not the binding (or not on this machine).</summary>
    public static nint CurrentDisplay()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(Lib, out _))
            return 0;
        return GetCurrentContext() == 0 ? 0 : GetCurrentDisplay();
    }

    public static string Text(nint p) => p == 0 ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    public static string ErrorWords(int error) => error switch
    {
        0x3009 => "EGL_BAD_MATCH",
        0x3004 => "EGL_BAD_ATTRIBUTE",
        0x3003 => "EGL_BAD_ALLOC",
        0x300C => "EGL_BAD_PARAMETER",
        0x3008 => "EGL_BAD_DISPLAY",
        _ => string.Create(CultureInfo.InvariantCulture, $"EGL error 0x{error:X4}"),
    };
}

/// <summary>The renderer's device as decoders see it on an EGL context (Linux): dmabuf imports, the render node a VAAPI device is
/// made on, and the (fourcc, modifier) pairs the display imports. Zero-copy is ready only when the display imports dmabufs and its
/// device names a render node; a software GL (llvmpipe) names none, and the words say so. VAAPI surfaces are always sampled in place
/// (there is no proof to run), so direct sampling counts as proven. Made on the render thread with the context current; read from
/// any thread (the import tables are fixed at birth; the failure table is under a lock).</summary>
public sealed unsafe class DmaBufShare : IGpuShare
{
    private static long _epochs;
    private static readonly string[] SoftwareRenderers = ["llvmpipe", "softpipe", "swrast", "SwiftShader", "lavapipe"];
    private readonly IGpuShare? _platform;
    private readonly Dictionary<uint, (ulong Modifier, bool ExternalOnly)[]> _modifiers;
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private DmaBufShare(IGpuShare? platform, string? node, bool ready, string words, string describe,
        Dictionary<uint, (ulong, bool)[]> modifiers)
    {
        _platform = platform;
        DrmRenderNode = node;
        Ready = ready;
        Words = words;
        Describe = describe;
        _modifiers = modifiers;
    }

    /// <summary>The share of the context current on this thread, or null when it is not an EGL context that imports dmabufs.</summary>
    public static DmaBufShare? ForCurrentContext(IGpuShare? platform, string renderer)
    {
        var display = EglImages.CurrentDisplay();
        if (display == 0)
            return null;
        var extensions = EglImages.Text(EglImages.QueryString(display, EglImages.Extensions));
        if (!extensions.Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal))
            return null;
        var modifiers = ImportTable(display, extensions.Contains("EGL_EXT_image_dma_buf_import_modifiers", StringComparison.Ordinal));
        var (node, software) = DeviceOf(display);
        // The device query is an extension a display may not answer; the GL's own name says a software rasteriser too.
        software |= SoftwareRenderers.Any(r => renderer.Contains(r, StringComparison.OrdinalIgnoreCase));
        var words = software ? $"the GL is software ({renderer}): zero-copy needs a GPU"
            : node is null ? "the EGL device names no DRM render node: VAAPI cannot be made on the renderer's GPU"
            : "";
        var describe = node is null ? $"EGL dmabuf imports on {renderer} (no render node)" : $"VAAPI → EGL dmabuf on {node} ({renderer})";
        return new DmaBufShare(platform, node, words.Length == 0, words, describe, modifiers);
    }

    /// <summary>Zero-copy decodes can be made on this renderer (a GPU's render node, dmabuf imports).</summary>
    public bool Ready { get; }

    /// <summary>Why zero-copy is not ready ("" when it is).</summary>
    public string Words { get; }

    public string ZeroCopyWords => Words;

    public GpuApi Api => GpuApi.DmaBuf;

    public string Describe { get; }

    public nint D3D11Device => 0;

    public long AdapterLuid => 0;

    public string? DrmRenderNode { get; }

    public long DeviceEpoch { get; } = Interlocked.Increment(ref _epochs);

    public bool DirectProven => true;

    /// <summary>The fourccs the display imports.</summary>
    public IReadOnlyCollection<uint> Fourccs => _modifiers.Keys;

    public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier)
    {
        if (!_modifiers.TryGetValue(fourcc, out var mods))
            return false;
        // The implicit modifier: the driver picks the layout it exported with (the import side takes it without a modifier).
        if (modifier == 0x00FFFFFFFFFFFFFF)
            return true;
        if (mods.Length == 0)
            return modifier == 0;
        foreach (var (m, externalOnly) in mods)
            if (m == modifier)
                return !externalOnly;
        return false;
    }

    public void Enter() => _platform?.Enter();

    public void Exit() => _platform?.Exit();

    public void ReportImportFailure(string sourceKey, string words)
    {
        lock (_gate)
            _failures[sourceKey] = words;
    }

    public bool ImportFailed(string sourceKey, out string words)
    {
        lock (_gate)
        {
            if (_failures.Count > 0 && _failures.TryGetValue(sourceKey, out var w))
            {
                words = w;
                return true;
            }
        }
        words = "";
        return false;
    }

    /// <summary>Forgets a source's import failure (it was opened again: it starts from the top of the ladder).</summary>
    public void Forget(string sourceKey)
    {
        lock (_gate)
            _failures.Remove(sourceKey);
    }

    private static Dictionary<uint, (ulong, bool)[]> ImportTable(nint display, bool withModifiers)
    {
        var table = new Dictionary<uint, (ulong, bool)[]>();
        var queryFormats = EglImages.GetProcAddress("eglQueryDmaBufFormatsEXT");
        var queryModifiers = EglImages.GetProcAddress("eglQueryDmaBufModifiersEXT");
        if (!withModifiers || queryFormats == 0)
        {
            // Without the query only linear layouts of the four plane formats are assumed (the import extension itself).
            foreach (var f in new uint[] { 0x20203852, 0x38385247, 0x20363152, 0x32335247 })
                table[f] = [];
            return table;
        }
        var formats = (delegate* unmanaged<nint, int, int*, int*, uint>)queryFormats;
        int count;
        if (formats(display, 0, null, &count) == 0 || count <= 0)
            return table;
        var list = new int[count];
        fixed (int* p = list)
            formats(display, count, p, &count);
        var modifiers = (delegate* unmanaged<nint, int, int, ulong*, uint*, int*, uint>)queryModifiers;
        foreach (var f in list.AsSpan(0, count))
        {
            var mods = Array.Empty<(ulong, bool)>();
            if (modifiers != null && modifiers(display, f, 0, null, null, &count) != 0 && count > 0)
            {
                var m = new ulong[count];
                var ext = new uint[count];
                fixed (ulong* pm = m)
                fixed (uint* pe = ext)
                    modifiers(display, f, count, pm, pe, &count);
                mods = new (ulong, bool)[count];
                for (var i = 0; i < count; i++)
                    mods[i] = (m[i], ext[i] != 0);
            }
            table[(uint)f] = mods;
        }
        return table;
    }

    private static (string? Node, bool Software) DeviceOf(nint display)
    {
        var queryDisplay = EglImages.GetProcAddress("eglQueryDisplayAttribEXT");
        var queryString = EglImages.GetProcAddress("eglQueryDeviceStringEXT");
        if (queryDisplay == 0 || queryString == 0)
            return (null, false);
        nint device = 0;
        if (((delegate* unmanaged<nint, int, nint*, uint>)queryDisplay)(display, EglImages.Device, &device) == 0 || device == 0)
            return (null, false);
        var deviceString = (delegate* unmanaged<nint, int, nint>)queryString;
        var extensions = EglImages.Text(deviceString(device, EglImages.Extensions));
        var software = extensions.Contains("EGL_MESA_device_software", StringComparison.Ordinal);
        string? node = null;
        if (extensions.Contains("EGL_EXT_device_drm_render_node", StringComparison.Ordinal))
        {
            var n = EglImages.Text(deviceString(device, EglImages.DrmRenderNodeFile));
            node = n.Length > 0 ? n : null;
        }
        return (node, software);
    }
}

/// <summary>Imports dmabuf frames into the context current on the render thread (EGL_EXT_image_dma_buf_import): one EGLImage per
/// plane, each the storage of a texture the importer owns (R8/RG8 for NV12, R16/RG16 for P010), sampled in place by the `yuv`
/// pass. After the frame's read of the planes is recorded, <see cref="Retain"/> holds the surface (one hold on its producer's slot)
/// behind a fence; <see cref="Collect"/> lets go of it, destroying its images, once the GPU has passed the fence. Two slots: the
/// frame being drawn and at most one in flight behind it, so the renderer never holds more than two; when both are in flight a new frame is not imported (the last
/// picture holds) rather than waiting for the GPU. Render thread only; nothing allocates per frame.</summary>
public sealed unsafe class DmaBufImporter : IDisposable
{
    private const int Slots = 2;

    private record struct Slot
    {
        public GpuTexture? Luma;
        public GpuTexture? Chroma;
        public nint LumaImage;
        public nint ChromaImage;
        public GpuSurface? Surface;
        public nint Fence;
        public bool Pending;
    }

    private readonly GlDevice _device;
    private readonly Slot[] _slots = new Slot[Slots];
    private readonly nint _display;
    private readonly nint _create;
    private readonly nint _destroy;
    private readonly int[] _attribs = new int[24];
    private int _current = -1;
    private bool _disposed;

    private DmaBufImporter(GlDevice device, nint display, nint create, nint destroy)
    {
        _device = device;
        _display = display;
        _create = create;
        _destroy = destroy;
    }

    /// <summary>An importer for the context current on this thread, or null with words when it is not an EGL context that imports
    /// dmabufs.</summary>
    public static DmaBufImporter? ForCurrentContext(GlDevice device, out string words)
    {
        ArgumentNullException.ThrowIfNull(device);
        words = "";
        var display = EglImages.CurrentDisplay();
        if (display == 0)
        {
            words = "the render context is not EGL: dmabuf frames cannot be imported";
            return null;
        }
        if (!EglImages.Text(EglImages.QueryString(display, EglImages.Extensions)).Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal))
        {
            words = "the EGL display does not import dmabufs (EGL_EXT_image_dma_buf_import)";
            return null;
        }
        var create = EglImages.GetProcAddress("eglCreateImageKHR");
        var destroy = EglImages.GetProcAddress("eglDestroyImageKHR");
        if (create == 0 || destroy == 0)
        {
            words = "EGL has no eglCreateImageKHR";
            return null;
        }
        return new DmaBufImporter(device, display, create, destroy);
    }

    /// <summary>Surfaces held behind a fence (imported, their reads not yet completed).</summary>
    public int Held
    {
        get
        {
            var n = 0;
            foreach (var s in _slots)
                if (s.Pending || s.Surface is not null)
                    n++;
            return n;
        }
    }

    /// <summary>Imports the surface's planes into the next free slot: true with the luma and chroma textures; false with words when
    /// the EGL import or the texture binding failed (<paramref name="busy"/> false: a real failure, the source steps down), or when
    /// every slot is still in flight (<paramref name="busy"/> true: hold the last picture, nothing failed).</summary>
    public bool TryImport(GpuSurface surface, out GpuTexture luma, out GpuTexture chroma, out bool busy, out string words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        luma = null!;
        chroma = null!;
        busy = false;
        words = "";
        Collect();
        if (surface.Api != GpuApi.DmaBuf || surface.PlaneCount != 2)
        {
            words = "not a two-plane dmabuf frame";
            return false;
        }
        var index = -1;
        for (var k = 1; k <= Slots; k++)
        {
            var i = (_current + k + Slots) % Slots;
            if (!_slots[i].Pending)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            busy = true;
            words = "every import slot is still being read by the GPU";
            return false;
        }
        ref var slot = ref _slots[index];
        ReleaseImages(ref slot);
        var p0 = surface.Planes[0];
        var p1 = surface.Planes[1];
        var lumaImage = Create(p0, out var error);
        var chromaImage = lumaImage == 0 ? 0 : Create(p1, out error);
        if (lumaImage == 0 || chromaImage == 0)
        {
            Destroy(lumaImage);
            words = $"eglCreateImageKHR refused a {DrmName(lumaImage == 0 ? p0.Fourcc : p1.Fourcc)} plane ({EglImages.ErrorWords(error)})";
            return false;
        }
        var ten = surface.Format == GpuSurfaceFormat.P010;
        slot.Luma = Ensure(slot.Luma, p0.Width, p0.Height, ten ? TextureFormat.R16 : TextureFormat.R8);
        slot.Chroma = Ensure(slot.Chroma, p1.Width, p1.Height, ten ? TextureFormat.Rg16 : TextureFormat.Rg8);
        if (!_device.BindImage(slot.Luma, lumaImage, EglImages.GetProcAddress) || !_device.BindImage(slot.Chroma, chromaImage, EglImages.GetProcAddress))
        {
            Destroy(lumaImage);
            Destroy(chromaImage);
            words = "the GL refused to sample an imported plane (glEGLImageTargetTexture2DOES)";
            return false;
        }
        slot.LumaImage = lumaImage;
        slot.ChromaImage = chromaImage;
        _current = index;
        luma = slot.Luma;
        chroma = slot.Chroma;
        return true;
    }

    /// <summary>After the frame's last read of the imported planes was recorded: holds the surface until the GPU has done it.</summary>
    public void Retain(GpuSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_current < 0)
            return;
        ref var slot = ref _slots[_current];
        surface.Retain();
        slot.Surface = surface;
        slot.Fence = _device.InsertFence();
        slot.Pending = true;
    }

    /// <summary>Lets go of every surface whose reads have completed (destroying its images). Never waits.</summary>
    public void Collect()
    {
        for (var i = 0; i < Slots; i++)
        {
            ref var slot = ref _slots[i];
            if (!slot.Pending || !_device.IsSignaled(slot.Fence))
                continue;
            _device.DeleteFence(slot.Fence);
            slot.Fence = 0;
            slot.Pending = false;
            ReleaseImages(ref slot);
        }
    }

    private void ReleaseImages(ref Slot slot)
    {
        Destroy(slot.LumaImage);
        Destroy(slot.ChromaImage);
        slot.LumaImage = 0;
        slot.ChromaImage = 0;
        var surface = slot.Surface;
        slot.Surface = null;
        surface?.Release();
    }

    private GpuTexture Ensure(GpuTexture? texture, int width, int height, TextureFormat format)
    {
        if (texture is not null && texture.Width == width && texture.Height == height && texture.Format == format)
            return texture;
        texture?.Dispose();
        return _device.AdoptExternal(width, height, format);
    }

    private nint Create(in DmaBufPlane plane, out int error)
    {
        var a = _attribs;
        var n = 0;
        a[n++] = EglImages.Width;
        a[n++] = plane.Width;
        a[n++] = EglImages.Height;
        a[n++] = plane.Height;
        a[n++] = EglImages.LinuxDrmFourcc;
        a[n++] = (int)plane.Fourcc;
        a[n++] = EglImages.Plane0Fd;
        a[n++] = plane.Fd;
        a[n++] = EglImages.Plane0Offset;
        a[n++] = (int)plane.Offset;
        a[n++] = EglImages.Plane0Pitch;
        a[n++] = plane.Pitch;
        if (plane.Modifier != 0x00FFFFFFFFFFFFFF)
        {
            a[n++] = EglImages.Plane0ModifierLo;
            a[n++] = (int)(plane.Modifier & 0xFFFFFFFF);
            a[n++] = EglImages.Plane0ModifierHi;
            a[n++] = (int)(plane.Modifier >> 32);
        }
        a[n] = EglImages.None;
        nint image;
        fixed (int* p = a)
            image = ((delegate* unmanaged<nint, nint, int, nint, int*, nint>)_create)(_display, 0, EglImages.LinuxDmaBuf, 0, p);
        error = image == 0 ? EglImages.GetError() : 0x3000;
        return image;
    }

    private void Destroy(nint image)
    {
        if (image != 0)
            _ = ((delegate* unmanaged<nint, nint, uint>)_destroy)(_display, image);
    }

    private static string DrmName(uint fourcc)
    {
        Span<char> c = stackalloc char[4];
        for (var i = 0; i < 4; i++)
            c[i] = (char)((fourcc >> (8 * i)) & 0xFF);
        return new string(c).TrimEnd();
    }

    /// <summary>Lets go of every surface (after the GPU finished with them: render thread, at the set's end) and deletes the textures.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _device.Finish();
        for (var i = 0; i < Slots; i++)
        {
            ref var slot = ref _slots[i];
            _device.DeleteFence(slot.Fence);
            slot.Fence = 0;
            slot.Pending = false;
            ReleaseImages(ref slot);
            slot.Luma?.Dispose();
            slot.Chroma?.Dispose();
            slot.Luma = null;
            slot.Chroma = null;
        }
    }
}
