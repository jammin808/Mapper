using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Platform;
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

    /// <summary>A surfaceless GL platform whose device other threads share through a lock.</summary>
    private sealed class SharedDevicePlatform : IGpuPlatform
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
        SharedDevicePlatform? made = null;
        var options = RenderHostOptions.Default with
        {
            Windows = false,
            Platform = new PlatformRequest("dxgi", Surfaceless: true),
            Dxgi = o => made = new SharedDevicePlatform(new GlPlatform(RenderContext.CreateSurfaceless(o))),
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
        var bytes = rig.Host.Stats.FramePathAllocatedBytes;
        _ = HoldFor(share, deviceLock, rig.Host, TimeSpan.FromMilliseconds(100));
        await rig.Host.FramesAsync(30).WaitAsync(HostRig.Patience);
        Assert.Equal(bytes, rig.Host.Stats.FramePathAllocatedBytes);
        log.WriteLine($"lock waits {deviceLock.Waits}, timed out {deviceLock.TimedOut}");
    }
}
