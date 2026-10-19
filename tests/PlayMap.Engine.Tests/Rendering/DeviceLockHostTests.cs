using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Presentation;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Gl;
using PlayMap.Render.Platform;
using PlayMap.Tests;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The render loop on a device other threads share (the DXGI platform's, decoders on it), here a GL platform wearing a fake
/// share whose Enter and Exit are the device's lock: uncontended, every frame takes the lock within its bound; a sharer holding the
/// device across many frames never stops the loop — each frame waits out its bound and is drawn and counted, and no frame lands while
/// the sharer holds the lock without a timed-out take to show for it; a decoder taking the lock bracket by bracket gets it between
/// frames; and the frame path stays allocation-free throughout.</summary>
[Collection("render host")]
public sealed class DeviceLockHostTests(ITestOutputHelper log)
{
    /// <summary>A share whose immediate-context lock is the platform's (as the DXGI platform's D3D11 share is).</summary>
    private sealed class LockedShare(GpuDeviceLock deviceLock) : IGpuShare
    {
        public GpuApi Api => GpuApi.D3D11;
        public string Describe => "a fake shared device";
        public nint D3D11Device => 0;
        public long AdapterLuid => 0;
        public string? DrmRenderNode => null;
        public long DeviceEpoch => 1;
        public bool DirectProven => false;
        public string ZeroCopyWords => "a fake shared device decodes nothing";
        public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier) => false;
        public void Enter() => deviceLock.Enter();
        public void Exit() => deviceLock.Exit();
        public void ReportImportFailure(string sourceKey, string words)
        {
            // Nothing imports on a fake device.
        }

        public bool ImportFailed(string sourceKey, out string words)
        {
            words = "";
            return false;
        }
    }

    /// <summary>A GL platform whose device other threads share through a lock: on a surfaceless context where the platform has one
    /// (EGL, Linux), else on the window system's hidden context.</summary>
    private sealed class SharedDevicePlatform : IGlPlatform
    {
        private readonly GlPlatform _gl;

        public SharedDevicePlatform(GlPlatform gl)
        {
            _gl = gl;
            Share = new LockedShare(Lock);
        }

        public GpuDeviceLock Lock { get; } = new();

        public string Name => "shared-device";

        public string Api => _gl.Api;

        public string Words => "";

        public RenderContext Context => _gl.Context;

        public IGpuDevice Device => _gl.Device;

        public PresentPath Path => PresentPath.Offscreen;

        public IGpuShare Share { get; }

        public GpuDeviceLock? DeviceLock => Lock;

        public IReadOnlyList<MonitorInfo> Monitors() => [];

        public IOutputPresenter CreatePresenter(OutputPresenterOptions options) =>
            throw new PlatformNotSupportedException("this platform renders offscreen");

        public void PumpEvents()
        {
            // No window system.
        }

        public void Dispose() => _gl.Dispose();
    }

    /// <summary>A decoder on the shared device: it opens at once and has no picture to give, and its close takes the device lock (as
    /// FFmpeg's begin–submit–end bracket does on the renderer's device), so it closes only once no other thread holds the lock.</summary>
    private sealed class DeviceSource(IGpuShare share) : IVideoSource
    {
        private int _closed;

        public string Key => "md_device";

        public bool Closed => Volatile.Read(ref _closed) != 0;

        public MediaInfo Info => MediaInfo.Unknown with { DurationNs = 2_000_000_000 };

        public SourceHealth Health => new(true, "ok", 0, 0, 0, 0, "a fake decoder on the shared device", false);

        public VideoFrameLease? Acquire(long mediaNs) => null;

        public void Prepare(long mediaNs, bool playing, double rate)
        {
            // Nothing to decode.
        }

        public bool IsReadyAt(long mediaNs) => false;

        public void Dispose()
        {
            share.Enter();
            try
            {
                Volatile.Write(ref _closed, 1);
            }
            finally
            {
                share.Exit();
            }
        }
    }

    /// <summary>The media edge handing out <see cref="DeviceSource"/>s on the platform's share.</summary>
    private sealed class DeviceMedia(Func<IGpuShare> share) : IMediaBackend
    {
        private DeviceSource? _opened;

        public DeviceSource? Opened => Volatile.Read(ref _opened);

        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("tests import nothing");

        public IVideoSource? Open(MediaItem item)
        {
            var source = new DeviceSource(share());
            Volatile.Write(ref _opened, source);
            return source;
        }

        public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

        public string? PathForHash(string contentHash) => null;
    }

    /// <summary>Holds the share's lock on a thread of its own for <paramref name="hold"/>: the frames drawn meanwhile and the frame
    /// takes that timed out meanwhile.</summary>
    private static (long Frames, long TimedOut) HoldFor(IGpuShare share, GpuDeviceLock deviceLock, RenderHost host, TimeSpan hold)
    {
        (long Frames, long TimedOut) seen = default;
        var thread = new Thread(() =>
        {
            share.Enter();
            try
            {
                var timedOut = deviceLock.TimedOut;
                var frames = host.Stats.Frames;
                using (var nap = new ManualResetEventSlim())
                    nap.Wait(hold);
                seen = (host.Stats.Frames - frames, deviceLock.TimedOut - timedOut);
            }
            finally
            {
                share.Exit();
            }
        })
        { IsBackground = true, Name = "sharer" };
        thread.Start();
        thread.Join();
        return seen;
    }

    [Fact]
    public async Task TheLoopDrawsInsideTheDeviceLockAndNeverWaitsPastItsBound()
    {
        // The device lock is every platform's: the shared device wears this machine's own GL context, surfaceless where there is one.
        SharedDevicePlatform? made = null;
        var surfaceless = OperatingSystem.IsLinux();
        var options = RenderHostOptions.Default with
        {
            Windows = false,
            Platform = new PlatformRequest("dxgi", Surfaceless: surfaceless),
            Dxgi = o => made = new SharedDevicePlatform(new GlPlatform(surfaceless ? RenderContext.CreateSurfaceless(o) : RenderContext.CreateHidden(o))),
        };
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)), options: options);
        Assert.NotNull(made);
        var deviceLock = made.Lock;
        var share = made.Share;
        await rig.Host.FramesAsync(10).WaitAsync(HostRig.Patience);

        // Uncontended: every frame takes the lock within its bound.
        var quiet = deviceLock.TimedOut;
        await rig.Host.FramesAsync(30).WaitAsync(HostRig.Patience);
        Assert.Equal(quiet, deviceLock.TimedOut);

        // A sharer holding the device for many frames: the loop goes on, each frame drawn past its bound and counted. A frame lands
        // inside the hold only after a take that timed out (one may have timed out just before the count was read).
        var (frames, timedOut) = HoldFor(share, deviceLock, rig.Host, TimeSpan.FromMilliseconds(400));
        log.WriteLine($"during a 400 ms hold: {frames} frames, {timedOut} timed-out takes");
        Assert.True(frames >= 5, $"the loop drew {frames} frames while a sharer held the device for 400 ms");
        Assert.True(frames <= timedOut + 1, $"{frames} frames landed inside the hold with {timedOut} timed-out takes");

        // A decoder taking the lock bracket by bracket gets it between frames while the loop draws on.
        var before = rig.Host.Stats.Frames;
        var decoder = Task.Factory.StartNew(() =>
        {
            using var nap = new ManualResetEventSlim();
            for (var i = 0; i < 60; i++)
            {
                share.Enter();
                try
                {
                    nap.Wait(1);
                }
                finally
                {
                    share.Exit();
                }
                nap.Wait(2);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await decoder.WaitAsync(HostRig.Patience);
        Assert.True(rig.Host.Stats.Frames > before, "the loop drew nothing while a decoder took the device bracket by bracket");

        // Warm, contended or not, the frame path allocates nothing.
        await rig.Host.FramesAsync(10).WaitAsync(HostRig.Patience);
        Allocations.Begin();
        var bytes = rig.Host.Stats.FramePathAllocatedBytes;
        _ = HoldFor(share, deviceLock, rig.Host, TimeSpan.FromMilliseconds(100));
        await rig.Host.FramesAsync(30).WaitAsync(HostRig.Patience);
        Assert.Equal(bytes, rig.Host.Stats.FramePathAllocatedBytes);
        log.WriteLine($"lock waits {deviceLock.Waits}, timed out {deviceLock.TimedOut}");
    }

    /// <summary>The stop lets go of the device lock before it waits for the sources to close: a decoder whose close takes the lock
    /// closes within the stop, the stop returns once it has, and no source is journalled as still closing.</summary>
    [Fact]
    public async Task TheStopLetsGoOfTheDeviceBeforeItWaitsForADecoderWhoseCloseTakesIt()
    {
        SharedDevicePlatform? made = null;
        var surfaceless = OperatingSystem.IsLinux();
        var options = RenderHostOptions.Default with
        {
            Windows = false,
            Platform = new PlatformRequest("dxgi", Surfaceless: surfaceless),
            Dxgi = o => made = new SharedDevicePlatform(new GlPlatform(surfaceless ? RenderContext.CreateSurfaceless(o) : RenderContext.CreateHidden(o))),
        };
        var media = new DeviceMedia(() => made!.Share);
        var clip = new MediaItem("md_device", "Device clip", MediaKind.Video, "/clips/device.mp4", "", 0, MediaInfo.Unknown, []);
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Media(clip.Id), 64, 36)) with { Media = [clip] };
        var rig = HostRig.Start(project, media, options);
        var hostStopped = false;
        try
        {
            Assert.True(await Host.Eventually.TrueAsync(() => media.Opened is not null, (int)HostRig.Patience.TotalMilliseconds), "the decoder never opened");
            await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);

            await rig.Host.DisposeAsync();
            hostStopped = true;
            Assert.True(media.Opened!.Closed, "the stop returned before the decoder closed");
            Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Kind == "fault");
        }
        finally
        {
            if (!hostStopped)
                await rig.Host.DisposeAsync();
            await rig.Engine.DisposeAsync();
        }
    }
}
