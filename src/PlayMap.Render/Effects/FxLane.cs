using System.Collections.Immutable;
using System.Text;
using PlayMap.Core.Effects;
using PlayMap.Core.Gpu;
using PlayMap.Core.Threading;
using PlayMap.Render.Engine;
using SkiaSharp;

namespace PlayMap.Render.Effects;

/// <summary>Where a device makes user programs, as the effects report it: the lane's mode and words, the ISF dialect it compiles, and
/// a hook a test holds a make open with. The GL backend's compile lane answers it; a device without one is described by
/// <see cref="FxLane"/> alone.</summary>
internal interface IProgramLane
{
    /// <summary>"shared-context" (a thread and a context of its own) or "render-thread" (one make a frame on the render thread).</summary>
    string LaneMode { get; }

    /// <summary>Why the lane runs where it does, when that is not on its own context ("" otherwise).</summary>
    string LaneWords { get; }

    /// <summary>The ISF dialect the device's programs are written in.</summary>
    IsfDialect Dialect { get; }

    /// <summary>Runs before each make with the program's name, and may block it there.</summary>
    Action<string>? BeforeMake { get; set; }

    /// <summary>Makes the lane now if there is none: on a context of its own when <paramref name="sharedContext"/> and the device can
    /// make one, else on the render thread. A lane of the other kind is made again when nothing is in flight on it. Render thread.</summary>
    void UseLane(bool sharedContext);
}

/// <summary>A picture an effect imports, decoded off the render thread: RGBA bytes, top row first.</summary>
internal sealed record FxDecodedImage(string Name, int Width, int Height, byte[] Rgba);

/// <summary>An effect's program ready on the render thread: the device's program and its layout, or why there is none (the stage
/// that refused and its words), and its imported pictures on the GPU. The lane lets go of it (the device releases the program after
/// the last frame that drew with it).</summary>
internal sealed class FxProgram
{
    public required string Key { get; init; }

    public ProgramId Id { get; init; }

    public ProgramLayout? Layout { get; init; }

    /// <summary>The refusal's words, or null when the program was made.</summary>
    public string? Error { get; init; }

    /// <summary>The stage that refused ("vertex", "fragment", "link", "layout", "source", "lane"), "" otherwise.</summary>
    public string Stage { get; init; } = "";

    public Dictionary<string, GpuTexture> Images { get; } = new(StringComparer.Ordinal);

    public long LastUsed;
}

/// <summary>The effects' programs on a device: one program per effect's files (keyed by their hash), asked of the device once
/// (<see cref="IGpuDevice.CreateProgramAsync"/>, compiled off the render thread), shared by every slot of those files, handed over
/// at a frame once made with its imported pictures decoded (off the render thread) and uploaded, and let go of once no slot has used
/// it for <see cref="UnusedFrames"/> frames (the device releases it after the last frame that drew with it). A file a picture of
/// which does not decode is refused with that picture's words; a compile's refusal comes first.
///
/// Threading: <see cref="Request"/>, <see cref="Get"/>, <see cref="Collect"/> and <see cref="Dispose"/> on the render thread;
/// <see cref="CheckAsync"/> and the counts from any thread.</summary>
public sealed class FxLane : IDisposable
{
    /// <summary>A program no slot has used for this many frames is let go.</summary>
    public const int UnusedFrames = 600;

    /// <summary>The most pixels an imported picture may have (4096 × 4096): its size is read from its header before it is decoded,
    /// so a small file that would unpack to gigabytes is refused, not decoded.</summary>
    internal const long MaxImagePixels = 4096L * 4096;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>An effect's imported pictures decoded, and the words of the first that did not decode (null when all did).</summary>
    private sealed record Decoded(ImmutableArray<FxDecodedImage> Images, string? Words);

    private sealed class Asked(string key, Task<ProgramMade> program, Task<Decoded>? images)
    {
        public string Key { get; } = key;
        public Task<ProgramMade> Program { get; } = program;
        public Task<Decoded>? Images { get; } = images;
        public bool Ready => Program.IsCompleted && (Images?.IsCompleted ?? true);
    }

    private readonly IGpuDevice _device;
    private readonly IProgramLane? _lane;
    private readonly Dictionary<string, Asked> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FxProgram> _programs = new(StringComparer.Ordinal);
    private readonly List<string> _keys = [];
    private int _pending;
    private int _held;
    private bool _hooked;
    private bool _disposed;

    /// <param name="device">The device the effects draw on.</param>
    /// <param name="sharedContext">False makes the device's programs on the render thread (the lane's fallback, or a test of it).</param>
    public FxLane(IGpuDevice device, bool sharedContext = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _lane = device as IProgramLane;
        _lane?.UseLane(sharedContext);
    }

    /// <summary>"shared-context" or "render-thread" on GL; "device" on a device that makes programs itself.</summary>
    public string Mode => _lane?.LaneMode ?? "device";

    /// <summary>Why the lane runs where it does, when that is not on its own context ("" otherwise).</summary>
    public string Words => _lane?.LaneWords ?? "";

    /// <summary>The ISF dialect this device compiles.</summary>
    public IsfDialect Dialect => _lane?.Dialect ?? IsfDialect.Glsl330;

    /// <summary>Runs before each make on the device's lane with the program's name, and may block it there: a test holds a compile
    /// open with it and counts the frames that go on meanwhile. The hook goes with this lane (<see cref="Dispose"/>).</summary>
    public Action<string>? BeforeCompile
    {
        get => _lane?.BeforeMake;
        set
        {
            if (_lane is null)
                return;
            _lane.BeforeMake = value;
            _hooked = value is not null;
        }
    }

    /// <summary>Programs asked for and not yet handed over.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Programs held for slots.</summary>
    public int Programs => Volatile.Read(ref _held);

    /// <summary>Asks for a program once (a key already asked for or held is not asked again). Render thread.</summary>
    internal void Request(string key, string name, string vertex, string fragment, ImmutableSortedDictionary<string, string> images)
    {
        if (_disposed || _programs.ContainsKey(key) || _asked.ContainsKey(key))
            return;
        var source = new ProgramSource(name, key, ProgramCode.Glsl, Utf8.GetBytes(vertex), Utf8.GetBytes(fragment), default);
        var program = _device.CreateProgramAsync(source, CancellationToken.None);
        var decoded = images.Count > 0 ? Compute.RunAsync(() => DecodeAll(images)) : null;
        _asked[key] = new Asked(key, program, decoded);
        Volatile.Write(ref _pending, _asked.Count);
    }

    /// <summary>The program for a key once handed over (null while it is made). Render thread.</summary>
    internal FxProgram? Get(string key, long frame)
    {
        if (!_programs.TryGetValue(key, out var e))
            return null;
        e.LastUsed = frame;
        return e;
    }

    /// <summary>Takes the programs the device has made (their pictures uploaded through <paramref name="list"/>); lets go of programs
    /// no slot has used for a while. Render thread, once a frame, inside the frame.</summary>
    public void Collect(long frame, ICommandList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_disposed)
            return;
        if (_asked.Count > 0)
        {
            foreach (var (key, asked) in _asked)
                if (asked.Ready)
                    _keys.Add(key);
            foreach (var key in _keys)
            {
                _asked.Remove(key, out var asked);
                Adopt(asked!, frame, list);
            }
            _keys.Clear();
            Volatile.Write(ref _pending, _asked.Count);
        }
        if (frame % 60 != 0)
            return;
        foreach (var (key, e) in _programs)
            if (frame - e.LastUsed > UnusedFrames)
                _keys.Add(key);
        foreach (var key in _keys)
        {
            Release(_programs[key]);
            _programs.Remove(key);
        }
        _keys.Clear();
        Volatile.Write(ref _held, _programs.Count);
    }

    private void Adopt(Asked asked, long frame, ICommandList list)
    {
#pragma warning disable VSTHRD002 // Both tasks have completed (Ready): Result reads them, it never waits.
        var made = asked.Program.IsCompletedSuccessfully
            ? asked.Program.Result
            : new ProgramMade(false, default, null, "lane", asked.Program.Exception?.GetBaseException().Message ?? "the make was cancelled");
        var decoded = asked.Images is { } task
            ? task.IsCompletedSuccessfully ? task.Result : new Decoded([], "the imported images did not decode")
            : null;
#pragma warning restore VSTHRD002
        FxProgram entry;
        if (!made.Ok)
        {
            entry = new FxProgram { Key = asked.Key, Error = made.Words, Stage = made.Stage, LastUsed = frame };
        }
        else if (decoded?.Words is { } words)
        {
            _device.ReleaseProgram(made.Id);
            entry = new FxProgram { Key = asked.Key, Error = words, LastUsed = frame };
        }
        else
        {
            entry = new FxProgram { Key = asked.Key, Id = made.Id, Layout = made.Layout, LastUsed = frame };
            if (decoded is not null)
            {
                foreach (var img in decoded.Images)
                {
                    var t = _device.CreateTexture(new TextureDesc(img.Width, img.Height, TextureFormat.Rgba8, FrameLists.Uploaded));
                    list.Upload(t, img.Rgba, UploadFormat.Rgba8);
                    entry.Images[img.Name] = t;
                }
            }
        }
        if (_programs.Remove(asked.Key, out var old))
            Release(old);
        _programs[asked.Key] = entry;
        Volatile.Write(ref _held, _programs.Count);
    }

    private void Release(FxProgram e)
    {
        if (e.Layout is not null)
            _device.ReleaseProgram(e.Id);
        foreach (var t in e.Images.Values)
            t.Dispose();
        e.Images.Clear();
    }

    /// <summary>Compiles sources for a report only (the program is let go at once): ok, or the device's words. Any thread; the
    /// outcome arrives once a frame has handed the make to the lane and taken it back.</summary>
    public async Task<FxCompileReport> CheckAsync(string name, string vertex, string fragment)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        ArgumentNullException.ThrowIfNull(fragment);
        if (_disposed)
            return new FxCompileReport(false, "the renderer is stopping", Mode);
        ProgramMade made;
        try
        {
            made = await _device.CreateProgramAsync(new ProgramSource(name, "", ProgramCode.Glsl, Utf8.GetBytes(vertex), Utf8.GetBytes(fragment), default),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new FxCompileReport(false, "the renderer is stopping", Mode);
        }
        if (made.Ok)
            _device.ReleaseProgram(made.Id);
        return new FxCompileReport(made.Ok, made.Ok ? null : made.Words, Mode);
    }

    /// <summary>Every imported picture decoded; the words of the first one that does not decode. Its boundary: whatever a decoder
    /// throws refuses that picture with its words.</summary>
    private static Decoded DecodeAll(ImmutableSortedDictionary<string, string> images)
    {
        var decoded = ImmutableArray.CreateBuilder<FxDecodedImage>(images.Count);
        string? why = null;
        foreach (var (name, data) in images)
        {
            string words;
            FxDecodedImage? img;
            try
            {
                img = Decode(name, data, out words);
            }
            catch (Exception ex)
            {
                (img, words) = (null, $"the imported image {name} does not decode: {ex.Message}");
            }
            if (img is not null)
                decoded.Add(img);
            else
                why ??= words;
        }
        return new Decoded(decoded.ToImmutable(), why);
    }

    private static FxDecodedImage? Decode(string name, string base64, out string why)
    {
        why = $"the imported image {name} does not decode (PNG or JPEG, base64)";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data))
        {
            if (codec is null)
                return null;
            if ((long)codec.Info.Width * codec.Info.Height > MaxImagePixels)
            {
                why = $"the imported image {name} is {codec.Info.Width} × {codec.Info.Height}; an effect's picture is at most 4096 × 4096";
                return null;
            }
        }
        using var decoded = SKBitmap.Decode(bytes);
        if (decoded is null || decoded.Width < 1 || decoded.Height < 1)
            return null;
        using var rgba = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!decoded.CopyTo(rgba, SKColorType.Rgba8888))
            return null;
        return new FxDecodedImage(name, rgba.Width, rgba.Height, rgba.Bytes);
    }

    /// <summary>Lets go of every program and picture held, and of every program still being made once it is (render thread). The
    /// device's lane stays: it is the device's.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hooked && _lane is not null)
            _lane.BeforeMake = null;
        foreach (var e in _programs.Values)
            Release(e);
        _programs.Clear();
        foreach (var asked in _asked.Values)
            _ = asked.Program.ContinueWith(static (t, state) =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Ok)
                    ((IGpuDevice)state!).ReleaseProgram(t.Result.Id);
            }, _device, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _asked.Clear();
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _held, 0);
    }
}
