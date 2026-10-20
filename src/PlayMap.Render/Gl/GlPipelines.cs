using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using PlayMap.Core;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;

namespace PlayMap.Render.Gl;

/// <summary>A program the GL backend draws: the linked program, its layout, and the program's uniform slot for every constant element
/// and texture of the layout, found once. A draw's block is set through them: every constant of the layout each draw (an unset one is
/// zero), each through the program's remembered value so an unchanged one costs no GL call; each texture slot's sampler points at the
/// unit of the slot's number, and the unit holds the draw's texture (none where the draw bound none).</summary>
internal sealed class GlProgram
{
    private readonly int[] _first;
    private readonly int[] _elements;
    private readonly int[] _samplers;

    public GlProgram(ProgramId id, ShaderProgram program, ProgramLayout layout)
    {
        Id = id;
        Program = program;
        Layout = layout;
        _first = new int[layout.Constants.Length];
        var elements = new List<int>();
        for (var c = 0; c < layout.Constants.Length; c++)
        {
            var slot = layout.Constants[c];
            _first[c] = elements.Count;
            if (slot.Count == 1)
            {
                elements.Add(program.SlotOf(slot.Name));
                continue;
            }
            for (var e = 0; e < slot.Count; e++)
                elements.Add(program.SlotOf(string.Create(CultureInfo.InvariantCulture, $"{slot.Name}[{e}]")));
        }
        _elements = [.. elements];
        _samplers = [.. layout.Textures.Select(t => program.SlotOf(t.Name))];
    }

    public ProgramId Id { get; }

    public ShaderProgram Program { get; }

    public ProgramLayout Layout { get; }

    /// <summary>The direct queue's value of the last frame that drew with it (a release waits for it).</summary>
    public ulong LastUse { get; set; }

    /// <summary>Sets every constant of the layout from the block and binds the textures by slot. Allocation-free.</summary>
    public void Apply(GlDevice device, ReadOnlySpan<byte> block, ReadOnlySpan<GpuTexture?> textures)
    {
        var f = MemoryMarshal.Cast<byte, float>(block);
        var n = MemoryMarshal.Cast<byte, int>(block);
        var constants = Layout.Constants;
        for (var c = 0; c < constants.Length; c++)
        {
            var slot = constants[c];
            var stride = ((ConstantTypes.Bytes(slot.Type) + 15) & ~15) / 4;
            var components = ConstantTypes.Components(slot.Type);
            for (var e = 0; e < slot.Count; e++)
            {
                var at = slot.Offset / 4 + e * stride;
                var uniform = _elements[_first[c] + e];
                switch (slot.Type)
                {
                    case ConstantType.Float or ConstantType.Float2 or ConstantType.Float3 or ConstantType.Float4:
                        Program.SetVectorAt(uniform, new Vector4(f[at], components > 1 ? f[at + 1] : 0, components > 2 ? f[at + 2] : 0,
                            components > 3 ? f[at + 3] : 0), components);
                        break;
                    case ConstantType.Int or ConstantType.Int2 or ConstantType.Int4:
                        Program.SetIntsAt(uniform, n[at], components > 1 ? n[at + 1] : 0, components > 2 ? n[at + 2] : 0,
                            components > 3 ? n[at + 3] : 0, components);
                        break;
                    case ConstantType.Float3x3:
                        Program.SetMatrixAt(uniform, new Matrix3x3(f[at], f[at + 1], f[at + 2], f[at + 4], f[at + 5], f[at + 6], f[at + 8],
                            f[at + 9], f[at + 10]));
                        break;
                    default:
                        Program.SetMatrixAt(uniform, new Matrix4x4(f[at], f[at + 1], f[at + 2], f[at + 3], f[at + 4], f[at + 5], f[at + 6],
                            f[at + 7], f[at + 8], f[at + 9], f[at + 10], f[at + 11], f[at + 12], f[at + 13], f[at + 14], f[at + 15]));
                        break;
                }
            }
        }
        var slots = Layout.Textures;
        for (var t = 0; t < slots.Length; t++)
        {
            var unit = slots[t].Slot;
            Program.SetIntsAt(_samplers[t], unit, 0, 0, 0, 1);
            device.BindUnit(unit, textures[unit] is { } texture ? texture.Id : 0);
        }
    }
}

/// <summary>A GL pipeline: a program and a state set, which the command list applies at each draw. Disposing one forgets it (the
/// program is the device's and stays).</summary>
internal sealed class GlPipeline : GpuPipeline
{
    private readonly GlPipelineCache _cache;

    public GlPipeline(GlPipelineCache cache, in PipelineKey key, GlProgram program)
        : base(key, program.Layout.Name, GlTexture.GlEpoch)
    {
        _cache = cache;
        Program = program;
    }

    public GlProgram Program { get; }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
            return;
        _cache.Forget(this);
        IsDisposed = true;
    }
}

/// <summary>The GL backend's pipelines and programs. On GL a pipeline is a linked program and a state set: "making" one is linking
/// its program if no pipeline of that program was made before. A warm-up links product programs from the library's GLSL on the
/// compile lane — its own context sharing the device's where one can be made, else the render thread, one job a frame — so a long
/// warm-up never holds a frame; user programs arrive linked from the lane too. A key the render thread draws before any warm-up made
/// it is made there, its program linked on the device's context if it was not, and counted (<see cref="RenderThreadMakes"/>).
///
/// Threading: <see cref="TryGet"/> and everything that makes or forgets, the render thread; <see cref="Count"/>,
/// <see cref="RenderThreadMakes"/> and <see cref="WarmAsync"/>, any thread.</summary>
internal sealed class GlPipelineCache : IPipelineCache
{
    private sealed record WarmJob(PipelineKey[] Keys, TaskCompletionSource<PipelineWarmResult> Done, CancellationToken Ct)
    {
        public int Next;
        public int Made;
        public int FromCache;
        public ImmutableArray<string>.Builder Failed { get; } = ImmutableArray.CreateBuilder<string>();
    }

    private readonly GlDevice _device;
    private readonly ProgramTable _table;
    private readonly Dictionary<PipelineKey, GlPipeline> _made = [];
    private readonly ConcurrentQueue<WarmJob> _warm = new();
    private readonly Func<FxCompileLane> _lane;
    private GlProgram?[] _programs;
    private WarmJob? _warming;
    private FxMaking? _linking;
    private string? _linkingName;
    private int _count;
    private long _renderThreadMakes;

    public GlPipelineCache(GlDevice device, ProgramTable table, Func<FxCompileLane> lane)
    {
        _device = device;
        _table = table;
        _lane = lane;
        _programs = new GlProgram?[table.ProductCount];
        Key = string.Create(CultureInfo.InvariantCulture,
            $"0 · {device.Renderer} {device.Version} · {BuildInfo.Current.Informational}");
    }

    /// <summary>"adapter LUID · driver · build": GL names no LUID (0); the driver is its renderer and version strings.</summary>
    public string Key { get; }

    public int Count => Volatile.Read(ref _count);

    public long RenderThreadMakes => Interlocked.Read(ref _renderThreadMakes);

    /// <summary>A product program's link is on the compile lane now (render thread).</summary>
    internal bool Linking => _linking is not null;

    public bool TryGet(in PipelineKey key, [NotNullWhen(true)] out GpuPipeline? pipeline)
    {
        if (_made.TryGetValue(key, out var made))
        {
            pipeline = made;
            return true;
        }
        pipeline = null;
        return false;
    }

    public Task<PipelineWarmResult> WarmAsync(IReadOnlyCollection<PipelineKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var done = new TaskCompletionSource<PipelineWarmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _warm.Enqueue(new WarmJob([.. keys], done, ct));
        return done.Task;
    }

    /// <summary>The pipeline a draw takes: the made one, else made here and counted. Render thread.</summary>
    public GlPipeline Resolve(in PipelineKey key)
    {
        if (_made.TryGetValue(key, out var made))
            return made;
        var pipeline = Make(key);
        Interlocked.Increment(ref _renderThreadMakes);
        return pipeline;
    }

    /// <summary>The program of an id: a product program linked on first use, or a user program the backend registered. Render
    /// thread.</summary>
    public GlProgram ProgramOf(ProgramId id)
    {
        if ((uint)id.Value < (uint)_programs.Length && _programs[id.Value] is { } program)
            return program;
        if (_table.IsUser(id))
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Program {id.Value} is no user program of this device (never made, or released)."), nameof(id));
        var layout = _table.Layout(id);
        var made = new GlProgram(id, _device.Program(layout.Name), layout);
        _programs[id.Value] = made;
        return made;
    }

    /// <summary>Takes a user program the lane made and the device adopted. Render thread.</summary>
    public void Register(GlProgram program)
    {
        var i = program.Id.Value;
        if (i >= _programs.Length)
            Array.Resize(ref _programs, Math.Max(i + 1, _programs.Length * 2));
        _programs[i] = program;
    }

    /// <summary>Forgets a user program and every pipeline made of it (the caller deletes the program). Render thread.</summary>
    public void Unregister(ProgramId id)
    {
        if ((uint)id.Value < (uint)_programs.Length)
            _programs[id.Value] = null;
        foreach (var key in _made.Keys.Where(k => k.Program == id).ToList())
            Forget(_made[key]);
    }

    public void Forget(GlPipeline pipeline)
    {
        if (_made.TryGetValue(pipeline.Key, out var held) && ReferenceEquals(held, pipeline) && _made.Remove(pipeline.Key))
            Volatile.Write(ref _count, _made.Count);
    }

    /// <summary>Runs the warm-ups asked for, in order: a key whose program is linked is made at once; a product program not yet linked
    /// is linked on the compile lane (its own context where it has one, else the render thread, one job a frame) and its keys made
    /// once the lane is done, one program at a time; a program a draw linked meanwhile is the draw's. A warm-up cancelled before it
    /// finished answers cancelled. Render thread, once a frame, after the lane's collect.</summary>
    public void Pump()
    {
        while (true)
        {
            if (_warming is null && !_warm.TryDequeue(out _warming))
                return;
            var job = _warming;
            if (job.Ct.IsCancellationRequested)
            {
                job.Done.TrySetCanceled(job.Ct);
                _warming = null;
                continue;
            }
            while (job.Next < job.Keys.Length)
            {
                var key = job.Keys[job.Next];
                if (_made.ContainsKey(key))
                {
                    job.FromCache++;
                    job.Next++;
                    continue;
                }
                if (!IsLinked(key.Program) && !Link(job, key.Program))
                    return;
                try
                {
                    Make(key);
                    job.Made++;
                }
                catch (Exception ex) when (ex is GlException or ArgumentException or KeyNotFoundException)
                {
                    job.Failed.Add(string.Create(CultureInfo.InvariantCulture, $"program {key.Program.Value}: {ex.Message}"));
                }
                job.Next++;
            }
            job.Done.TrySetResult(new PipelineWarmResult(job.Keys.Length, job.Made, job.FromCache, job.Failed.ToImmutable()));
            _warming = null;
        }
    }

    /// <summary>Whether a draw of the program needs no link first: a program made here, or a product program the device linked.</summary>
    private bool IsLinked(ProgramId id) =>
        ((uint)id.Value < (uint)_programs.Length && _programs[id.Value] is not null)
        || (!_table.IsUser(id) && (uint)id.Value < (uint)_table.ProductCount && _device.HasProgram(_table.Names[id.Value]));

    /// <summary>Moves a product program's link on, one program at a time: takes what the lane linked into the device (a program a draw
    /// linked meanwhile keeps the draw's, and the lane's is deleted), then asks the lane for the next. True once the program is linked;
    /// false while the lane works, and when the lane refused it (the refusal written into the warm-up, the key skipped).</summary>
    private bool Link(WarmJob job, ProgramId id)
    {
        if (_table.IsUser(id) || (uint)id.Value >= (uint)_table.ProductCount)
            return true;
        var name = _table.Names[id.Value];
        if (_linking is not null)
        {
            if (_linking.Made is not { } made)
                return false;
            var linked = _linkingName!;
            _linking = null;
            _linkingName = null;
            if (made.ProgramId != 0 && !_device.AdoptLibraryProgram(linked, made.ProgramId))
                _device.AdoptProgram(made.ProgramId, linked).Dispose();
            if (linked == name && made.ProgramId == 0)
            {
                job.Failed.Add(string.Create(CultureInfo.InvariantCulture, $"program {name} ({made.Stage}): {made.Words}"));
                job.Next++;
                return false;
            }
            if (_device.HasProgram(name))
                return true;
        }
        var (vertex, fragment) = Shaders.ShaderLibrary.Source(name);
        _linking = _lane().Make(name, vertex, fragment, reflect: false);
        _linkingName = name;
        return false;
    }

    /// <summary>Deletes every user program still registered and forgets every pipeline (the device is going).</summary>
    public void DisposeUserPrograms()
    {
        for (var i = _table.ProductCount; i < _programs.Length; i++)
        {
            _programs[i]?.Program.Dispose();
            _programs[i] = null;
        }
        _made.Clear();
        Volatile.Write(ref _count, 0);
    }

    /// <summary>Answers every warm-up still waiting as cancelled and deletes a program the lane linked for one and nobody took (the
    /// device is going; the lane has stopped).</summary>
    public void Abandon()
    {
        if (_linking?.Made is { ProgramId: not 0 } orphan)
            _device.AdoptProgram(orphan.ProgramId, _linkingName ?? "warm-up").Dispose();
        _linking = null;
        _linkingName = null;
        _warming?.Done.TrySetCanceled(_warming.Ct);
        _warming = null;
        while (_warm.TryDequeue(out var job))
            job.Done.TrySetCanceled(job.Ct);
    }

    private GlPipeline Make(in PipelineKey key)
    {
        var pipeline = new GlPipeline(this, key, ProgramOf(key.Program));
        _made[key] = pipeline;
        Volatile.Write(ref _count, _made.Count);
        return pipeline;
    }
}
