using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Media.Hw;

namespace PlayMap.Media.Tests.Hw;

/// <summary>The hand-declared DRM PRIME descriptor (libavutil/hwcontext_drm.h, FFmpeg 9; AV_DRM_MAX_PLANES = 4) and the D3D11VA
/// device context FFmpeg.AutoGen binds (libavutil/hwcontext_d3d11va.h), held to the header's layout: a changed FFmpeg fails here
/// instead of corrupting memory. Then the descriptor read into a surface as VAAPI exports it (vaExportSurfaceHandle with separate
/// layers: NV12 as R8 + GR88, P010 as R16 + GR32 — the DRM fourccs of drm_fourcc.h), the composed-layer form, and refusals.</summary>
public sealed unsafe class DrmLayoutTests
{
    [Fact]
    public void TheDrmDescriptorsHaveTheHeadersLayout()
    {
        Assert.Equal(24, sizeof(AVDRMObjectDescriptor));
        Assert.Equal(24, sizeof(AVDRMPlaneDescriptor));
        Assert.Equal(104, sizeof(AVDRMLayerDescriptor));
        Assert.Equal(528, sizeof(AVDRMFrameDescriptor));
        Assert.Equal(8, Offset<AVDRMObjectDescriptor>("Size"));
        Assert.Equal(16, Offset<AVDRMObjectDescriptor>("FormatModifier"));
        Assert.Equal(8, Offset<AVDRMPlaneDescriptor>("Offset"));
        Assert.Equal(16, Offset<AVDRMPlaneDescriptor>("Pitch"));
        Assert.Equal(4, Offset<AVDRMLayerDescriptor>("PlaneCount"));
        Assert.Equal(8, Offset<AVDRMLayerDescriptor>("Plane0"));
        Assert.Equal(8, Offset<AVDRMFrameDescriptor>("Object0"));
        Assert.Equal(104, Offset<AVDRMFrameDescriptor>("LayerCount"));
        Assert.Equal(112, Offset<AVDRMFrameDescriptor>("Layer0"));
    }

    [Fact]
    public void TheD3d11vaDeviceContextHasTheHeadersLayout()
    {
        Assert.Equal(64, sizeof(AVD3D11VADeviceContext));
        Assert.Equal(32, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>("lock"));
        Assert.Equal(48, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>("lock_ctx"));
        Assert.Equal(56, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>("BindFlags"));
        Assert.Equal(60, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>("MiscFlags"));
    }

    [Fact]
    public void FourccsAreTheFourCharacters()
    {
        Assert.Equal("R8", DrmFourcc.Name(DrmFourcc.R8));
        Assert.Equal("GR88", DrmFourcc.Name(DrmFourcc.Gr88));
        Assert.Equal("R16", DrmFourcc.Name(DrmFourcc.R16));
        Assert.Equal("GR32", DrmFourcc.Name(DrmFourcc.Gr32));
        Assert.Equal("NV12", DrmFourcc.Name(DrmFourcc.Nv12));
        Assert.Equal("P010", DrmFourcc.Name(DrmFourcc.P010));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    // As VAAPI exports a 1920×1080 surface with separate layers: one object (the dmabuf), the chroma at an offset into it.
    private static AVDRMFrameDescriptor Separate(uint luma, uint chroma, int pitch, ulong modifier)
    {
        var d = new AVDRMFrameDescriptor { ObjectCount = 1, LayerCount = 2 };
        d.Object0 = new AVDRMObjectDescriptor { Fd = 42, Size = 4 << 20, FormatModifier = modifier };
        d.Layer0 = new AVDRMLayerDescriptor { Format = luma, PlaneCount = 1, Plane0 = new AVDRMPlaneDescriptor { ObjectIndex = 0, Offset = 0, Pitch = pitch } };
        d.Layer1 = new AVDRMLayerDescriptor { Format = chroma, PlaneCount = 1, Plane0 = new AVDRMPlaneDescriptor { ObjectIndex = 0, Offset = pitch * 1088, Pitch = pitch } };
        return d;
    }

    [Fact]
    public void SeparateNv12LayersBecomeTwoPlanesAtTheirVisibleSizes()
    {
        var d = Separate(DrmFourcc.R8, DrmFourcc.Gr88, 2048, 0x0100000000000002);
        var s = new GpuSurface();
        Assert.True(DrmFrames.ToSurface(&d, 1920, 1080, s, out var words), words);
        Assert.Equal((GpuApi.DmaBuf, GpuSurfaceFormat.Nv12, 2, 1920, 1080), (s.Api, s.Format, s.PlaneCount, s.CodedWidth, s.CodedHeight));
        Assert.Equal(new DmaBufPlane(42, DrmFourcc.R8, 1920, 1080, 0, 2048, 0x0100000000000002), s.Planes[0]);
        Assert.Equal(new DmaBufPlane(42, DrmFourcc.Gr88, 960, 540, 2048 * 1088, 2048, 0x0100000000000002), s.Planes[1]);
    }

    [Fact]
    public void SeparateP010LayersAndAnOddSizeRoundChromaUp()
    {
        var d = Separate(DrmFourcc.R16, DrmFourcc.Gr32, 4096, DrmFourcc.ModLinear);
        var s = new GpuSurface();
        Assert.True(DrmFrames.ToSurface(&d, 1919, 1079, s, out var words), words);
        Assert.Equal(GpuSurfaceFormat.P010, s.Format);
        Assert.Equal((1919, 1079), (s.Planes[0].Width, s.Planes[0].Height));
        Assert.Equal((960, 540, DrmFourcc.Gr32), (s.Planes[1].Width, s.Planes[1].Height, s.Planes[1].Fourcc));
    }

    [Fact]
    public void AComposedNv12LayerIsImportedAsTheSeparateFourccs()
    {
        var d = new AVDRMFrameDescriptor { ObjectCount = 2, LayerCount = 1 };
        d.Object0 = new AVDRMObjectDescriptor { Fd = 5, FormatModifier = DrmFourcc.ModInvalid };
        d.Object1 = new AVDRMObjectDescriptor { Fd = 6, FormatModifier = DrmFourcc.ModInvalid };
        d.Layer0 = new AVDRMLayerDescriptor
        {
            Format = DrmFourcc.Nv12,
            PlaneCount = 2,
            Plane0 = new AVDRMPlaneDescriptor { ObjectIndex = 0, Offset = 0, Pitch = 1280 },
            Plane1 = new AVDRMPlaneDescriptor { ObjectIndex = 1, Offset = 64, Pitch = 1280 },
        };
        var s = new GpuSurface();
        Assert.True(DrmFrames.ToSurface(&d, 1280, 720, s, out var words), words);
        Assert.Equal((5, DrmFourcc.R8), (s.Planes[0].Fd, s.Planes[0].Fourcc));
        Assert.Equal((6, DrmFourcc.Gr88, 64L, 640, 360), (s.Planes[1].Fd, s.Planes[1].Fourcc, s.Planes[1].Offset, s.Planes[1].Width, s.Planes[1].Height));
    }

    [Fact]
    public void A422LayoutAndBrokenDescriptorsAreRefusedWithWords()
    {
        var y210 = new AVDRMFrameDescriptor { ObjectCount = 1, LayerCount = 1 };
        y210.Object0 = new AVDRMObjectDescriptor { Fd = 3 };
        y210.Layer0 = new AVDRMLayerDescriptor { Format = 0x30313259, PlaneCount = 1, Plane0 = new AVDRMPlaneDescriptor { Pitch = 7680 } };
        var s = new GpuSurface();
        Assert.False(DrmFrames.ToSurface(&y210, 1920, 1080, s, out var words));
        Assert.Contains("Y210", words, StringComparison.Ordinal);
        Assert.Contains("not a 4:2:0 layout", words, StringComparison.Ordinal);

        var badObject = Separate(DrmFourcc.R8, DrmFourcc.Gr88, 2048, 0);
        badObject.Layer1 = badObject.Layer1 with { Plane0 = badObject.Layer1.Plane0 with { ObjectIndex = 3 } };
        Assert.False(DrmFrames.ToSurface(&badObject, 1920, 1080, s, out words));
        Assert.Contains("object the frame does not have", words, StringComparison.Ordinal);

        var empty = new AVDRMFrameDescriptor();
        Assert.False(DrmFrames.ToSurface(&empty, 16, 16, s, out words));
        Assert.Equal("the mapped frame has no DRM layers", words);
        Assert.False(DrmFrames.ToSurface(null, 16, 16, s, out _));
    }
}
