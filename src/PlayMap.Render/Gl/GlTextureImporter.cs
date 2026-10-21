using PlayMap.Core.Gpu;
using PlayMap.Core.Media;

namespace PlayMap.Render.Gl;

/// <summary>Decoders' surfaces as textures on the GL backend: dmabuf frames through EGL images (<see cref="DmaBufImporter"/>, on an
/// EGL context that imports them) and Direct3D 11 frames through ANGLE (<see cref="AngleImporter"/>, on the DXGI platform's context,
/// whose share is the device's import share). Whether the context imports a kind at all is asked once and kept. Each reader imports
/// through an import of its own (<see cref="OpenImport"/>) whose importers, made the first time a surface of their kind arrives, and
/// slots are its own and live as long as it does; nothing is let go for a reader's frame numbers or for a source's silence.
///
/// The planes are ready at once (one context orders the import before the draw that reads it: <see cref="ImportedPlanes.Ready"/> is
/// <see cref="FenceValue.None"/>), the crop is the visible picture and the coded size the planes' (ANGLE's slices carry the
/// decoder's padding). An import that fails is told to the device's import share against the surface's source
/// (<see cref="GlDevice.ImportShare"/>: the source then steps down and sends its next frame through memory). A dmabuf is held from
/// its import (the lease still alive) until the GPU passed the submit of the frame that imported it (the fence that submit puts
/// behind it), then let go as a later frame begins; an ANGLE slice is released as soon as its read is recorded (one device, one
/// immediate context). A reader's import disposed while it still holds surfaces lets go of them as their fences pass, without
/// waiting on the GPU.
///
/// Threading: the render thread, with the device's context current.</summary>
internal sealed class GlTextureImporter : ITextureImporter, IDisposable
{
    private readonly GlDevice _device;
    private readonly List<GlTextureImport> _imports = [];
    private bool _dmabufAsked;
    private string? _dmabufWords;
    private bool _disposed;

    public GlTextureImporter(GlDevice device)
    {
        _device = device;
    }

    /// <summary>Readers' imports open now, or closed and still holding surfaces.</summary>
    public int Imports => _imports.Count;

    /// <summary>Dmabuf importers made since the backend was made (one per reader that imported a dmabuf).</summary>
    public int DmabufImportersMade { get; private set; }

    internal GlDevice Device => _device;

    public bool CanImport(GpuSurface surface, out string? words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (surface.Api)
        {
            case GpuApi.DmaBuf:
                if (!_dmabufAsked)
                {
                    _dmabufAsked = true;
                    _dmabufWords = DmaBufImporter.Refusal(out _, out _, out _);
                }
                words = _dmabufWords;
                return words is null;
            case GpuApi.D3D11:
                words = !OperatingSystem.IsWindows() ? "Direct3D 11 frames are imported through ANGLE, on Windows only"
                    : _device.ImportShare is null ? "the renderer shares no D3D11 device with the decoders"
                    : null;
                return words is null;
            default:
                words = $"this renderer imports no {surface.Api} frames";
                return false;
        }
    }

    public ITextureImport OpenImport()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var import = new GlTextureImport(this);
        _imports.Add(import);
        return import;
    }

    /// <summary>An import that failed with words is told to the import share against the surface's source; false either way (no
    /// words: every slot was in flight, nothing failed).</summary>
    internal bool Failed(GpuSurface surface, string? words)
    {
        if (words is not null)
            _device.ImportShare?.ReportImportFailure(surface.SourceKey, words);
        return false;
    }

    internal DmaBufImporter? MakeDmabuf(out string words)
    {
        var made = DmaBufImporter.ForCurrentContext(_device, out words);
        if (made is not null)
            DmabufImportersMade++;
        return made;
    }

    /// <summary>Surfaces held now, every reader's (their reads not yet completed on the GPU).</summary>
    public int Held
    {
        get
        {
            var held = 0;
            foreach (var import in _imports)
                held += import.Held;
            return held;
        }
    }

    /// <summary>After the frame's submit: fences every dmabuf hold the frame took, every reader's.</summary>
    public void Submitted()
    {
        foreach (var import in _imports)
            import.FenceImported();
    }

    /// <summary>As a frame begins: lets go of every dmabuf whose reads completed, and of the importers of a reader's import that was
    /// disposed once it holds nothing.</summary>
    public void Collect()
    {
        for (var i = _imports.Count - 1; i >= 0; i--)
        {
            var import = _imports[i];
            import.Collect();
            if (import.Closed && import.Held == 0)
            {
                import.Release();
                _imports.RemoveAt(i);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The device's end: whatever is still held is waited for once, then every reader's importers go.
        if (Held > 0)
            _device.Finish();
        foreach (var import in _imports)
            import.Release();
        _imports.Clear();
    }
}

/// <summary>One reader's imports on the GL backend: its own dmabuf importer (two slots) and ANGLE importer, made the first time a
/// surface of their kind arrives, and the slots it imported into since the last submit (fenced at that submit). Disposed, it imports
/// nothing more; its importer goes once its holds are let go (<see cref="GlTextureImporter.Collect"/>).
///
/// Threading: the render thread.</summary>
internal sealed class GlTextureImport : ITextureImport
{
    private readonly GlTextureImporter _owner;
    private readonly int[] _unfenced = new int[2];
    private int _unfencedCount;
    private DmaBufImporter? _dmabuf;
    private string? _dmabufWords;
    private IDisposable? _angle;
    private string? _angleWords;

    public GlTextureImport(GlTextureImporter owner)
    {
        _owner = owner;
    }

    /// <summary>Disposed by its reader (its importers may still hold surfaces).</summary>
    public bool Closed { get; private set; }

    public int Held => _dmabuf?.Held ?? 0;

    public bool TryImport(GpuSurface surface, out ImportedPlanes planes, out string? words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(Closed, this);
        planes = default;
        if (!_owner.CanImport(surface, out words))
            return _owner.Failed(surface, words);
        GpuTexture luma, chroma;
        int codedWidth = surface.Width, codedHeight = surface.Height;
        if (surface.Api == GpuApi.DmaBuf)
        {
            if (Dmabuf() is not { } dmabuf)
            {
                words = _dmabufWords;
                return _owner.Failed(surface, words);
            }
            if (!dmabuf.TryImport(surface, out luma, out chroma, out var busy, out var why, out var slot))
            {
                words = busy ? null : why;
                return _owner.Failed(surface, words);
            }
            if (_unfencedCount < _unfenced.Length)
                _unfenced[_unfencedCount++] = slot;
        }
        else
        {
            if (!OperatingSystem.IsWindows())
                return false;
            if (!ImportAngle(surface, out luma, out chroma, out codedWidth, out codedHeight, out words))
                return _owner.Failed(surface, words);
        }
        planes = new ImportedPlanes(luma, chroma, FenceValue.None, surface.Width, surface.Height)
        {
            CodedWidth = codedWidth,
            CodedHeight = codedHeight,
        };
        words = null;
        return true;
    }

    /// <summary>At the frame's submit: the fence behind every hold this reader took in the frame.</summary>
    internal void FenceImported()
    {
        for (var i = 0; i < _unfencedCount; i++)
            _dmabuf?.Fence(_unfenced[i]);
        _unfencedCount = 0;
    }

    /// <summary>Lets go of the holds whose reads completed.</summary>
    internal void Collect() => _dmabuf?.Collect();

    private DmaBufImporter? Dmabuf()
    {
        if (_dmabuf is null && _dmabufWords is null)
        {
            _dmabuf = _owner.MakeDmabuf(out var words);
            if (_dmabuf is null)
                _dmabufWords = words;
        }
        return _dmabuf;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool ImportAngle(GpuSurface surface, out GpuTexture luma, out GpuTexture chroma, out int codedWidth, out int codedHeight,
        out string? words)
    {
        if (_angle is null && _angleWords is null)
        {
            _angle = AngleImporter.ForCurrentContext(_owner.Device, _owner.Device.ImportShare!, out var made);
            if (_angle is null)
                _angleWords = made;
        }
        if (_angle is not AngleImporter angle)
        {
            luma = chroma = null!;
            codedWidth = codedHeight = 0;
            words = _angleWords;
            return false;
        }
        var ok = angle.TryImport(surface, out luma, out chroma, out codedWidth, out codedHeight, out var why);
        words = ok ? null : why;
        return ok;
    }

    /// <summary>Lets go of the importers (their holds let go first: every read completed, or the device is going).</summary>
    internal void Release()
    {
        _dmabuf?.Dispose();
        _angle?.Dispose();
        _dmabuf = null;
        _angle = null;
    }

    /// <summary>The reader is done: no import from now on. The ANGLE importer goes now (it holds nothing past a recorded read); the
    /// dmabuf importer goes now if it holds nothing, else once the GPU has finished with what it holds (the owner's collect).</summary>
    public void Dispose()
    {
        if (Closed)
            return;
        Closed = true;
        _angle?.Dispose();
        _angle = null;
        _dmabuf?.Collect();
        if (Held > 0)
            return;
        _dmabuf?.Dispose();
        _dmabuf = null;
    }
}
