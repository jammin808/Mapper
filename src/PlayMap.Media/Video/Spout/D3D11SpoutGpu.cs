using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Media.Video.Spout;

/// <summary>The COM slots and structures Spout's device uses (the Windows SDK's x64 layout; checked against the mingw-w64 headers by
/// SpoutLayoutTests, as the renderer's are by DxgiLayoutTests).</summary>
[SupportedOSPlatform("windows")]
internal static class SpoutCom
{
    public const int QueryInterface = 0;
    public const int Release = 2;
    public const int DeviceCreateTexture2D = 5;
    public const int DeviceOpenSharedResource = 28;
    public const int DeviceGetImmediateContext = 40;
    public const int ContextMap = 14;
    public const int ContextUnmap = 15;
    public const int ContextCopyResource = 47;
    public const int ContextUpdateSubresource = 48;
    public const int ContextFlush = 111;
    public const int Texture2DGetDesc = 10;
    public const int ResourceGetSharedHandle = 8;
    public const int KeyedMutexAcquireSync = 8;
    public const int KeyedMutexReleaseSync = 9;

    public static readonly Guid IidTexture2D = new(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);
    public static readonly Guid IidDxgiResource = new(0x035f3ab4, 0x482e, 0x4e50, 0xb4, 0x1f, 0x8a, 0x7f, 0x8b, 0xd8, 0x96, 0x0b);
    public static readonly Guid IidKeyedMutex = new(0x9d8e1289, 0xd7b3, 0x465f, 0x81, 0x26, 0x25, 0x0e, 0x34, 0x9a, 0xf8, 0x5d);
}

/// <summary>D3D11_TEXTURE2D_DESC.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct SpoutTextureDesc(uint Width, uint Height, uint MipLevels, uint ArraySize, uint Format, uint SampleCount,
    uint SampleQuality, uint Usage, uint BindFlags, uint CpuAccessFlags, uint MiscFlags);

/// <summary>D3D11_MAPPED_SUBRESOURCE.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct SpoutMapped(nint Data, uint RowPitch, uint DepthPitch);

/// <summary>Spout's own Direct3D 11 device (Windows): on the default adapter, or WARP. Sender textures are made shared (legacy
/// share handles, as Spout publishes them: <c>IDXGIResource::GetSharedHandle</c>, truncated to 32 bits) and a published one opened
/// with <c>OpenSharedResource</c>; a received texture is copied into three staging textures and mapped with DO_NOT_WAIT (a copy still
/// in flight is "pending", never waited for). The immediate context is used by one thread at a time per texture (the device is
/// multithread-protected for the rest).</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class D3D11SpoutGpu : ISpoutGpu
{
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 5;
    private const uint CreateBgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint UsageDefault = 0;
    private const uint UsageStaging = 3;
    private const uint BindShaderResource = 0x8;
    private const uint BindRenderTarget = 0x20;
    private const uint CpuAccessRead = 0x20000;
    private const uint MiscShared = 0x2;
    private const uint MapRead = 1;
    private const uint MapFlagDoNotWait = 0x100000;
    private const int ErrorWasStillDrawing = unchecked((int)0x887A000A);
    private const int WaitTimeout = 0x102;

    private nint _device;
    private nint _context;
    private readonly Lock _gate = new();

    private D3D11SpoutGpu(nint device, nint context, bool warp)
    {
        _device = device;
        _context = context;
        Describe = warp ? "Direct3D 11 · WARP" : "Direct3D 11 · default adapter";
    }

    public string Describe { get; }

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* levels, uint levelCount,
        uint sdkVersion, nint* device, int* level, nint* context);

    /// <summary>Makes the device; throws <see cref="InvalidOperationException"/> with words when Direct3D 11 makes none.</summary>
    public static D3D11SpoutGpu Create(bool warp)
    {
        int* levels = stackalloc int[2] { 0xb100, 0xb000 };
        nint device = 0, context = 0;
        int level = 0;
        var hr = D3D11CreateDevice(0, warp ? DriverTypeWarp : DriverTypeHardware, 0, CreateBgraSupport, levels, 2, SdkVersion, &device, &level, &context);
        if (hr < 0)
            throw new InvalidOperationException($"Direct3D 11 made no device for Spout ({Hr(hr)})");
        return new D3D11SpoutGpu(device, context, warp);
    }

    internal static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture);

    internal static void* Slot(nint obj, int slot) => (*(void***)obj)[slot];

    internal static void Release(ref nint obj)
    {
        if (obj == 0)
            return;
        _ = ((delegate* unmanaged<nint, uint>)Slot(obj, SpoutCom.Release))(obj);
        obj = 0;
    }

    internal static nint Query(nint obj, Guid iid)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(obj, SpoutCom.QueryInterface))(obj, &iid, &result);
        return hr < 0 ? 0 : result;
    }

    private nint MakeTexture(int width, int height, uint format, uint usage, uint bind, uint cpu, uint misc)
    {
        var desc = new SpoutTextureDesc((uint)width, (uint)height, 1, 1, format, 1, 0, usage, bind, cpu, misc);
        nint texture = 0;
        var hr = ((delegate* unmanaged<nint, SpoutTextureDesc*, nint, nint*, int>)Slot(_device, SpoutCom.DeviceCreateTexture2D))(_device, &desc, 0, &texture);
        if (hr < 0)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Direct3D 11 made no {width}×{height} texture of format {format} ({Hr(hr)})"));
        return texture;
    }

    public ISpoutTexture? Open(uint handle, out string words)
    {
        nint texture = 0;
        var iid = SpoutCom.IidTexture2D;
        var hr = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)Slot(_device, SpoutCom.DeviceOpenSharedResource))(_device, (nint)handle, &iid, &texture);
        if (hr < 0 || texture == 0)
        {
            words = hr == unchecked((int)0x80070057)
                ? "the sender's texture is on another GPU (or gone): run both apps on the same GPU"
                : $"OpenSharedResource failed ({Hr(hr)})";
            return null;
        }
        SpoutTextureDesc desc = default;
        ((delegate* unmanaged<nint, SpoutTextureDesc*, void>)Slot(texture, SpoutCom.Texture2DGetDesc))(texture, &desc);
        words = "";
        try
        {
            return new Texture(this, texture, handle, (int)desc.Width, (int)desc.Height, desc.Format, staging: true);
        }
        catch
        {
            Release(ref texture);
            throw;
        }
    }

    public ISpoutTexture Create(int width, int height, uint format)
    {
        var texture = MakeTexture(width, height, format, UsageDefault, BindShaderResource | BindRenderTarget, 0, MiscShared);
        var resource = Query(texture, SpoutCom.IidDxgiResource);
        nint shared = 0;
        if (resource != 0)
        {
            _ = ((delegate* unmanaged<nint, nint*, int>)Slot(resource, SpoutCom.ResourceGetSharedHandle))(resource, &shared);
            Release(ref resource);
        }
        if (shared == 0)
        {
            Release(ref texture);
            throw new InvalidOperationException("Direct3D 11 gave no share handle for Spout's texture");
        }
        lock (_gate)
            ((delegate* unmanaged<nint, void>)Slot(_context, SpoutCom.ContextFlush))(_context);
        // Spout publishes the legacy handle's low 32 bits (they are all that is used).
        return new Texture(this, texture, (uint)(ulong)shared, width, height, format, staging: false);
    }

    public void Dispose()
    {
        Release(ref _context);
        Release(ref _device);
    }

    private sealed class Texture : ISpoutTexture
    {
        private readonly D3D11SpoutGpu _gpu;
        private readonly nint[] _staging = new nint[SpoutReceiver.StagingSlots];
        private nint _texture;
        private nint _keyed;

        public Texture(D3D11SpoutGpu gpu, nint texture, uint handle, int width, int height, uint format, bool staging)
        {
            _gpu = gpu;
            _texture = texture;
            Handle = handle;
            Width = width;
            Height = height;
            Format = format;
            _keyed = Query(texture, SpoutCom.IidKeyedMutex);
            if (!staging)
                return;
            for (var i = 0; i < _staging.Length; i++)
                _staging[i] = gpu.MakeTexture(width, height, format, UsageStaging, 0, CpuAccessRead, 0);
        }

        public uint Handle { get; }

        public int Width { get; }

        public int Height { get; }

        public uint Format { get; }

        public bool Keyed => _keyed != 0;

        public nint Native => _texture;

        public LockTry AcquireKeyed()
        {
            var hr = ((delegate* unmanaged<nint, ulong, uint, int>)Slot(_keyed, SpoutCom.KeyedMutexAcquireSync))(_keyed, 0, 0);
            return hr == WaitTimeout || hr < 0 ? LockTry.Busy : LockTry.Acquired;
        }

        public void ReleaseKeyed() => _ = ((delegate* unmanaged<nint, ulong, int>)Slot(_keyed, SpoutCom.KeyedMutexReleaseSync))(_keyed, 0);

        public void CopyToStaging(int slot)
        {
            var target = _staging[slot];
            if (target == 0)
                throw new InvalidOperationException("this texture has no staging copies (a sender's own)");
            lock (_gpu._gate)
                ((delegate* unmanaged<nint, nint, nint, void>)Slot(_gpu._context, SpoutCom.ContextCopyResource))(_gpu._context, target, _texture);
        }

        public StagingRead ReadStaging(int slot, Span<byte> destination, int stride)
        {
            var staging = _staging[slot];
            if (staging == 0)
                return StagingRead.Failed;
            lock (_gpu._gate)
            {
                SpoutMapped mapped = default;
                var hr = ((delegate* unmanaged<nint, nint, uint, uint, uint, SpoutMapped*, int>)Slot(_gpu._context, SpoutCom.ContextMap))(
                    _gpu._context, staging, 0, MapRead, MapFlagDoNotWait, &mapped);
                if (hr == ErrorWasStillDrawing)
                    return StagingRead.Pending;
                if (hr < 0)
                    return StagingRead.Failed;
                var row = Width * 4;
                for (var y = 0; y < Height && (y + 1) * stride <= destination.Length; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.Data + (long)y * mapped.RowPitch, row).CopyTo(destination.Slice(y * stride, row));
                ((delegate* unmanaged<nint, nint, uint, void>)Slot(_gpu._context, SpoutCom.ContextUnmap))(_gpu._context, staging, 0);
                return StagingRead.Ready;
            }
        }

        public void Upload(ReadOnlySpan<byte> pixels, int stride)
        {
            if (pixels.Length < stride * Height)
                throw new ArgumentException("fewer rows than the texture holds", nameof(pixels));
            fixed (byte* p = pixels)
            {
                lock (_gpu._gate)
                    ((delegate* unmanaged<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(_gpu._context, SpoutCom.ContextUpdateSubresource))(
                        _gpu._context, _texture, 0, 0, p, (uint)stride, 0);
            }
        }

        public void Flush()
        {
            lock (_gpu._gate)
                ((delegate* unmanaged<nint, void>)Slot(_gpu._context, SpoutCom.ContextFlush))(_gpu._context);
        }

        public void Dispose()
        {
            for (var i = 0; i < _staging.Length; i++)
                Release(ref _staging[i]);
            Release(ref _keyed);
            Release(ref _texture);
        }
    }
}
