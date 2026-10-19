using PlayMap.Core.Gpu;
using PlayMap.Core.Media;

namespace PlayMap.Render.Gl;

/// <summary>Decoders' surfaces as textures on the GL backend: dmabuf frames through EGL images (<see cref="DmaBufImporter"/>, on an
/// EGL context that imports them) and Direct3D 11 frames through ANGLE (<see cref="AngleImporter"/>, on the DXGI platform's context,
/// whose share is the device's import share). Each source (<see cref="GpuSurface.SourceKey"/>) has importers of its own, made the
/// first time a surface of their kind arrives from it, with their own slots; a source that sent nothing for <see cref="IdleFrames"/>
/// frames and holds no surface is let go. The planes are ready at once (one context orders the import before the draw that reads it:
/// <see cref="ImportedPlanes.Ready"/> is <see cref="FenceValue.None"/>), the crop is the visible picture and the coded size the
/// planes' (ANGLE's slices carry the decoder's padding). An import that fails is told to the device's import share against the
/// surface's source (<see cref="GlDevice.ImportShare"/>: the source then steps down and sends its next frame through memory). A dmabuf is held from the submit of the frame that imported it until the GPU passed that
/// frame (its fence), then let go as a later frame begins; an ANGLE slice is released as soon as its read is recorded (one device,
/// one immediate context).
///
/// Threading: the render thread, with the device's context current.</summary>
internal sealed class GlTextureImporter : ITextureImporter, IDisposable
{
    /// <summary>Frames a source may send nothing before its importers are let go (when they hold no surface).</summary>
    public const int IdleFrames = 600;

    private sealed class Source
    {
        public DmaBufImporter? Dmabuf;
        public string? DmabufWords;
        public IDisposable? Angle;
        public string? AngleWords;
        public long LastFrame;
    }

    private readonly GlDevice _device;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly List<(Source Source, GpuSurface Surface)> _imported = new(4);
    private readonly List<string> _stale = [];
    private long _frame;
    private bool _disposed;

    public GlTextureImporter(GlDevice device)
    {
        _device = device;
    }

    /// <summary>Sources with importers now.</summary>
    public int Sources => _sources.Count;

    public bool CanImport(GpuSurface surface, out string? words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (surface.Api)
        {
            case GpuApi.DmaBuf:
            {
                var source = SourceOf(surface);
                words = Dmabuf(source) is null ? source.DmabufWords : null;
                return words is null;
            }
            case GpuApi.D3D11:
            {
                if (!OperatingSystem.IsWindows())
                {
                    words = "Direct3D 11 frames are imported through ANGLE, on Windows only";
                    return false;
                }
                var source = SourceOf(surface);
                words = Angle(source) is null ? source.AngleWords : null;
                return words is null;
            }
            default:
                words = $"this renderer imports no {surface.Api} frames";
                return false;
        }
    }

    public bool TryImport(GpuSurface surface, out ImportedPlanes planes, out string? words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        planes = default;
        if (!CanImport(surface, out words))
            return Failed(surface, words);
        var source = SourceOf(surface);
        GpuTexture luma, chroma;
        int codedWidth = surface.Width, codedHeight = surface.Height;
        if (surface.Api == GpuApi.DmaBuf)
        {
            if (!source.Dmabuf!.TryImport(surface, out luma, out chroma, out var busy, out var why))
            {
                words = busy ? null : why;
                return Failed(surface, words);
            }
            _imported.Add((source, surface));
        }
        else
        {
            if (!OperatingSystem.IsWindows())
                return false;
            if (!ImportAngle(source, surface, out luma, out chroma, out codedWidth, out codedHeight, out words))
                return Failed(surface, words);
        }
        planes = new ImportedPlanes(luma, chroma, FenceValue.None, surface.Width, surface.Height)
        {
            CodedWidth = codedWidth,
            CodedHeight = codedHeight,
        };
        words = null;
        return true;
    }

    /// <summary>An import that failed with words is told to the import share against the surface's source; false either way (no
    /// words: every slot was in flight, nothing failed).</summary>
    private bool Failed(GpuSurface surface, string? words)
    {
        if (words is not null)
            _device.ImportShare?.ReportImportFailure(surface.SourceKey, words);
        return false;
    }

    /// <summary>Surfaces held now, every source's (their reads not yet completed on the GPU).</summary>
    public int Held
    {
        get
        {
            var held = 0;
            foreach (var source in _sources.Values)
                held += source.Dmabuf?.Held ?? 0;
            return held;
        }
    }

    /// <summary>After the frame's submit: holds every dmabuf the frame imported behind the frame's fence.</summary>
    public void Submitted()
    {
        foreach (var (source, surface) in _imported)
            source.Dmabuf!.Retain(surface);
        _imported.Clear();
    }

    /// <summary>As a frame begins: lets go of every dmabuf whose reads completed, and of the importers of a source that sent
    /// nothing for <see cref="IdleFrames"/> frames and holds nothing.</summary>
    public void Collect(long frame)
    {
        _frame = frame;
        foreach (var (key, source) in _sources)
        {
            source.Dmabuf?.Collect();
            if (frame - source.LastFrame > IdleFrames && (source.Dmabuf?.Held ?? 0) == 0)
                _stale.Add(key);
        }
        foreach (var key in _stale)
        {
            Release(_sources[key]);
            _sources.Remove(key);
        }
        _stale.Clear();
    }

    private Source SourceOf(GpuSurface surface)
    {
        if (!_sources.TryGetValue(surface.SourceKey, out var source))
            _sources[surface.SourceKey] = source = new Source();
        source.LastFrame = _frame;
        return source;
    }

    private DmaBufImporter? Dmabuf(Source source)
    {
        if (source.Dmabuf is null && source.DmabufWords is null)
        {
            source.Dmabuf = DmaBufImporter.ForCurrentContext(_device, out var words);
            if (source.Dmabuf is null)
                source.DmabufWords = words;
        }
        return source.Dmabuf;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private AngleImporter? Angle(Source source)
    {
        if (source.Angle is null && source.AngleWords is null)
        {
            if (_device.ImportShare is not { } share)
            {
                source.AngleWords = "the renderer shares no D3D11 device with the decoders";
                return null;
            }
            source.Angle = AngleImporter.ForCurrentContext(_device, share, out var words);
            if (source.Angle is null)
                source.AngleWords = words;
        }
        return source.Angle as AngleImporter;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool ImportAngle(Source source, GpuSurface surface, out GpuTexture luma, out GpuTexture chroma, out int codedWidth,
        out int codedHeight, out string? words)
    {
        var ok = Angle(source)!.TryImport(surface, out luma, out chroma, out codedWidth, out codedHeight, out var why);
        words = ok ? null : why;
        return ok;
    }

    private static void Release(Source source)
    {
        source.Dmabuf?.Dispose();
        source.Angle?.Dispose();
        source.Dmabuf = null;
        source.Angle = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _imported.Clear();
        foreach (var source in _sources.Values)
            Release(source);
        _sources.Clear();
        _disposed = true;
    }
}
