using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Numerics;
using PlayMap.Render.Decode;
using PlayMap.Render.Gpu;
using PlayMap.Render.Video;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Decode;

/// <summary>The GL import half of zero-copy on llvmpipe (EGL_EXT_image_dma_buf_import; the planes are memfds, which llvmpipe imports
/// by mapping them): NV12 and P010 frames with odd sizes, non-zero offsets and 256-byte-aligned pitches, imported as one EGLImage
/// per plane (R8 + GR88, R16 + GR32 — the DRM fourccs), convert to the same RGB16F picture as the CPU upload of the same bytes,
/// bit for bit. The importer holds each surface until its fence passes and never more than two; an import that fails keeps the
/// last picture, reports the failure against the frame's source and says why. The export half (EGL_MESA_image_dma_buf_export,
/// a GL texture exported as a dmabuf and imported back) runs where the display offers it, else the test says it is absent. Dmabufs,
/// memfds and EGL's surfaceless display are Linux's: elsewhere every test is skipped with the words.</summary>
[Collection("egl")]
public sealed partial class DmaBufImportTests(EglFixture egl, ITestOutputHelper log)
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "memfd_create", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MemfdCreate(string name, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static partial int Ftruncate(int fd, long length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "pwrite", SetLastError = true)]
    private static unsafe partial nint Pwrite(int fd, byte* buffer, nuint count, long offset);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    /// <summary>One frame as a decoder would export it: both planes in one memfd, luma at <c>LumaOffset</c>, chroma after it.</summary>
    private sealed class Frame : IDisposable
    {
        public required int Fd;
        public required byte[] Bytes;
        public required int Width;
        public required int Height;
        public required int Pitch;
        public required long LumaOffset;
        public required long ChromaOffset;
        public required bool TenBit;
        public GCHandle Pin;

        public void Dispose()
        {
            if (Pin.IsAllocated)
                Pin.Free();
            if (Fd >= 0)
                _ = Close(Fd);
            Fd = -1;
        }
    }

    private static unsafe Frame Make(int width, int height, int pitch, long lumaOffset, bool tenBit, ulong seed)
    {
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;
        var bps = tenBit ? 2 : 1;
        var chromaOffset = (lumaOffset + (long)pitch * height + 255) / 256 * 256;
        var total = chromaOffset + (long)pitch * ch;
        var bytes = new byte[total];
        var rng = new Pcg32(seed, 7);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Sample(bytes, lumaOffset + (long)y * pitch + x * bps, rng.NextInt(1024), tenBit);
        for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw * 2; x++)
                Sample(bytes, chromaOffset + (long)y * pitch + x * bps, rng.NextInt(1024), tenBit);
        var fd = MemfdCreate("playmap-dmabuf-test", 0);
        Assert.True(fd >= 0, "memfd_create failed: " + Marshal.GetLastPInvokeError());
        Assert.Equal(0, Ftruncate(fd, total));
        fixed (byte* p = bytes)
            Assert.Equal((nint)total, Pwrite(fd, p, (nuint)total, 0));
        return new Frame
        {
            Fd = fd, Bytes = bytes, Width = width, Height = height, Pitch = pitch, LumaOffset = lumaOffset, ChromaOffset = chromaOffset,
            TenBit = tenBit, Pin = GCHandle.Alloc(bytes, GCHandleType.Pinned),
        };
    }

    // 8-bit: the low byte of a 10-bit value; P010: ten significant bits in the high bits of a little-endian 16-bit sample.
    private static void Sample(byte[] bytes, long at, int value, bool tenBit)
    {
        if (!tenBit)
        {
            bytes[at] = (byte)(16 + (value % 220));
            return;
        }
        var v = (ushort)((64 + (value % 876)) << 6);
        bytes[at] = (byte)v;
        bytes[at + 1] = (byte)(v >> 8);
    }

    private static GpuSurface Surface(Frame f, IGpuSurfaceOwner? owner, string key = "md_test")
    {
        var s = new GpuSurface
        {
            Api = GpuApi.DmaBuf, Format = f.TenBit ? GpuSurfaceFormat.P010 : GpuSurfaceFormat.Nv12, Width = f.Width, Height = f.Height,
            CodedWidth = f.Width, CodedHeight = f.Height, PlaneCount = 2, SourceKey = key, Owner = owner,
        };
        s.Planes[0] = new DmaBufPlane(f.Fd, f.TenBit ? 0x20363152u : 0x20203852u, f.Width, f.Height, f.LumaOffset, f.Pitch, 0);
        s.Planes[1] = new DmaBufPlane(f.Fd, f.TenBit ? 0x32335247u : 0x38385247u, (f.Width + 1) / 2, (f.Height + 1) / 2, f.ChromaOffset, f.Pitch, 0);
        return s;
    }

    private static FramePlane[] CpuPlanes(Frame f)
    {
        var base0 = f.Pin.AddrOfPinnedObject();
        return
        [
            new FramePlane(base0 + (nint)f.LumaOffset, f.Pitch, f.Width, f.Height),
            new FramePlane(base0 + (nint)f.ChromaOffset, f.Pitch, (f.Width + 1) / 2, (f.Height + 1) / 2),
        ];
    }

    private static float[] Read(GlDevice device, VideoTextureSet set)
    {
        var target = set.Converted!;
        var px = new float[target.Width * target.Height * 4];
        device.ReadPixelsFloat(target, px);
        return px;
    }

    [LinuxTheory]
    [InlineData(1918, 1078, 2048, 256, false)]
    [InlineData(1920, 1080, 2048, 0, false)]
    [InlineData(1280, 720, 2816, 512, true)]
    [InlineData(641, 361, 1536, 4096, true)]
    public void AnImportedFrameConvertsToTheCpuUploadsPictureBitForBit(int width, int height, int pitch, long offset, bool tenBit)
    {
        using var frame = Make(width, height, pitch, offset, tenBit, (ulong)(width * 31 + height));
        egl.Run(ctx =>
        {
            var device = ctx.Device;
            var probe = DmaBufImporter.ForCurrentContext(device, out var words);
            Assert.True(probe is not null, "this EGL display does not import dmabufs: " + words);
            probe.Dispose();
            var layout = tenBit ? PixelLayout.P010 : PixelLayout.Nv12;
            using var cpu = new VideoTextureSet(device);
            Assert.True(cpu.Update(new TestLease(layout, width, height, 1, CpuPlanes(frame))));
            var expected = Read(device, cpu);

            var owner = new CountingOwner();
            using var gpu = new VideoTextureSet(device);
            Assert.True(gpu.Update(new TestLease(layout, width, height, 1, surface: Surface(frame, owner))), gpu.Refusal);
            Assert.Equal((1, 0), (gpu.Imports, gpu.ImportFailures));
            var actual = Read(device, gpu);
            Assert.Equal(expected.Length, actual.Length);
            var differ = 0;
            for (var i = 0; i < expected.Length; i++)
                if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                    differ++;
            Assert.Equal(0, differ);
            // Held past the lease until the GPU has read it; let go once its fence passed.
            Assert.Equal(1, owner.Holds);
            device.Finish();
            gpu.Update(new TestLease(layout, width, height, 1, surface: Surface(frame, owner)));
            Assert.Equal(0, owner.Holds);
        });
    }

    [LinuxFact]
    public void TheRendererNeverHoldsMoreThanTwoSurfacesAndHoldsTheLastPictureWhenBothAreInFlight()
    {
        using var frame = Make(320, 180, 512, 0, false, 99);
        egl.Run(ctx =>
        {
            var device = ctx.Device;
            var owner = new CountingOwner();
            using var set = new VideoTextureSet(device);
            var shown = 0;
            for (var g = 1; g <= 12; g++)
            {
                if (set.Update(new TestLease(PixelLayout.Nv12, 320, 180, g, surface: Surface(frame, owner))))
                    shown++;
                Assert.InRange(set.HeldSurfaces, 0, 2);
                Assert.InRange(owner.Holds, 0, 2);
            }
            Assert.True(shown >= 1);
            Assert.Equal(0, set.ImportFailures);
            Assert.True(owner.Peak <= 2, $"peak {owner.Peak} held");
            device.Finish();
        });
    }

    [LinuxFact]
    public void AFailedImportKeepsTheLastPictureReportsTheSourceAndSaysWhy()
    {
        using var frame = Make(256, 144, 256, 0, false, 5);
        egl.Run(ctx =>
        {
            var device = ctx.Device;
            var share = new RecordingShare();
            device.DecodeContext = new DecodeContext(share, new ZeroCopyPlatform(GpuApi.DmaBuf, false, true, "test"), "egl", null, "");
            try
            {
                var owner = new CountingOwner();
                using var set = new VideoTextureSet(device);
                Assert.True(set.Update(new TestLease(PixelLayout.Nv12, 256, 144, 1, surface: Surface(frame, owner, "md_a"))));
                var before = Read(device, set);
                var picture = set.Picture;

                // A plane with no width (EGL_EXT_image_dma_buf_import: EGL_BAD_PARAMETER): EGL refuses the image.
                var broken = Surface(frame, owner, "md_a");
                broken.Planes[0] = broken.Planes[0] with { Width = 0 };
                Assert.False(set.Update(new TestLease(PixelLayout.Nv12, 256, 144, 2, surface: broken)));
                Assert.Same(picture, set.Picture);
                Assert.Equal(before, Read(device, set));
                Assert.Equal(1, set.ImportFailures);
                Assert.StartsWith("eglCreateImageKHR refused a R8 plane", set.Refusal, StringComparison.Ordinal);
                Assert.True(share.ImportFailed("md_a", out var words));
                Assert.Equal(set.Refusal, words);
                Assert.False(share.ImportFailed("md_b", out _));

                // The source stepped down: its next frame comes through memory and is drawn, no black frame between.
                Assert.True(set.Update(new TestLease(PixelLayout.Nv12, 256, 144, 3, CpuPlanes(frame))));
                Assert.Equal(before, Read(device, set));
                Assert.Null(set.Refusal);
                device.Finish();
            }
            finally
            {
                device.DecodeContext = null;
            }
        });
    }

    [LinuxFact]
    public void TheShareOfThisEglDisplayImportsThePlaneFormatsAndSaysWhyZeroCopyIsNotReady()
    {
        egl.Run(ctx =>
        {
            var share = DmaBufShare.ForCurrentContext(null, ctx.Device.Renderer);
            Assert.NotNull(share);
            log.WriteLine($"{share.Describe}: ready {share.Ready} — {share.Words}");
            Assert.Equal(GpuApi.DmaBuf, share.Api);
            Assert.True(share.CanImport(GpuSurfaceFormat.Nv12, 0x20203852, 0x00FFFFFFFFFFFFFF), "R8 with the implicit modifier");
            Assert.True(share.CanImport(GpuSurfaceFormat.Nv12, 0x38385247, 0), "GR88 linear");
            Assert.False(share.CanImport(GpuSurfaceFormat.Nv12, 0x30313259, 0), "Y210 is not a plane format here");
            if (ctx.Device.Renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(share.Ready);
                Assert.Null(share.DrmRenderNode);
                Assert.StartsWith("the GL is software", share.Words, StringComparison.Ordinal);
                var choice = DecodeLadder.Choose(DecodePolicy.Auto, DecodeLadder.Platform(share, share.Ready, share.Words));
                Assert.Equal((DecodePath.HwCopyBack, share.Words), (choice.Ceiling, choice.Why));
            }
            share.ReportImportFailure("md_x", "EGL_BAD_MATCH");
            Assert.True(share.ImportFailed("md_x", out var w) && w == "EGL_BAD_MATCH");
            share.Forget("md_x");
            Assert.False(share.ImportFailed("md_x", out _));
        });
    }

    [LinuxFact]
    public void ATextureExportedAsADmabufImportsBackUnchangedWhereMesaExportsIt()
    {
        egl.Run(ctx =>
        {
            var display = EglImages.CurrentDisplay();
            var extensions = EglImages.Text(EglImages.QueryString(display, EglImages.Extensions));
            if (!extensions.Contains("EGL_MESA_image_dma_buf_export", StringComparison.Ordinal))
            {
                // Said, not skipped silently: the import half runs on memfds in the tests above.
                log.WriteLine("EGL_MESA_image_dma_buf_export is absent on this display: the export round trip is compile-only here");
                return;
            }
            ExportRoundTrip(ctx.Device, display);
        });
    }

    private static unsafe void ExportRoundTrip(GlDevice device, nint display)
    {
        const int w = 64, h = 32;
        var luma = new byte[w * h];
        var chroma = new byte[w / 2 * (h / 2) * 2];
        var rng = new Pcg32(3, 1);
        for (var i = 0; i < luma.Length; i++)
            luma[i] = (byte)(16 + rng.NextInt(219));
        for (var i = 0; i < chroma.Length; i++)
            chroma[i] = (byte)(16 + rng.NextInt(224));
        using var y = device.CreateTexture(w, h, TextureFormat.R8);
        using var c = device.CreateTexture(w / 2, h / 2, TextureFormat.Rg8);
        device.Upload(y, luma, UploadFormat.R8);
        device.Upload(c, chroma, UploadFormat.Rg8);
        var create = (delegate* unmanaged<nint, nint, int, nint, int*, nint>)EglImages.GetProcAddress("eglCreateImageKHR");
        var query = (delegate* unmanaged<nint, nint, int*, int*, ulong*, uint>)EglImages.GetProcAddress("eglExportDMABUFImageQueryMESA");
        var export = (delegate* unmanaged<nint, nint, int*, int*, int*, uint>)EglImages.GetProcAddress("eglExportDMABUFImageMESA");
        var planes = new DmaBufPlane[2];
        var images = new nint[2];
        var destroy = (delegate* unmanaged<nint, nint, uint>)EglImages.GetProcAddress("eglDestroyImageKHR");
        var textures = new[] { y, c };
        for (var i = 0; i < 2; i++)
        {
            var none = EglImages.None;
            var image = create(display, EglImages.GetCurrentContext(), EglImages.GlTexture2D, (nint)textures[i].Id, &none);
            Assert.NotEqual(0, image);
            images[i] = image;
            int fourcc, count, fd, stride, offset;
            ulong modifier;
            Assert.NotEqual(0u, query(display, image, &fourcc, &count, &modifier));
            Assert.NotEqual(0u, export(display, image, &fd, &stride, &offset));
            planes[i] = new DmaBufPlane(fd, (uint)fourcc, textures[i].Width, textures[i].Height, offset, stride, modifier);
        }
        var s = new GpuSurface { Api = GpuApi.DmaBuf, Format = GpuSurfaceFormat.Nv12, Width = w, Height = h, CodedWidth = w, CodedHeight = h, PlaneCount = 2 };
        s.Planes[0] = planes[0];
        s.Planes[1] = planes[1];
        using var set = new VideoTextureSet(device);
        Assert.True(set.Update(new TestLease(PixelLayout.Nv12, w, h, 1, surface: s)), set.Refusal);
        var handles = new[] { GCHandle.Alloc(luma, GCHandleType.Pinned), GCHandle.Alloc(chroma, GCHandleType.Pinned) };
        try
        {
            using var cpu = new VideoTextureSet(device);
            Assert.True(cpu.Update(new TestLease(PixelLayout.Nv12, w, h, 1,
                [new FramePlane(handles[0].AddrOfPinnedObject(), w, w, h), new FramePlane(handles[1].AddrOfPinnedObject(), w, w / 2, h / 2)])));
            Assert.Equal(Read(device, cpu), Read(device, set));
        }
        finally
        {
            foreach (var handle in handles)
                handle.Free();
            device.Finish();
            foreach (var p in planes)
                _ = Close(p.Fd);
            foreach (var image in images)
                _ = destroy(display, image);
        }
    }
}
