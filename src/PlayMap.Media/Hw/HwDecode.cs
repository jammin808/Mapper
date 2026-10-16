using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Hw;

/// <summary>The zero-copy half of opening a hardware decoder: a device made on the renderer's own GPU, so its surfaces import
/// without leaving the GPU. Linux makes a VAAPI device on the render node the renderer's EGL display names (never "renderD128" by
/// guess: on a machine with two GPUs only the renderer's imports); Windows adopts the renderer's ID3D11Device (wp2-d3d11's share)
/// with decoder and shader-resource binding and the share's recursive lock. The frame side maps a VAAPI frame to DRM PRIME
/// (av_hwframe_map with read access, which syncs the surface first) and reads a D3D11 frame's texture array and slice.
/// Written to FFmpeg's public headers (hwcontext.h, hwcontext_drm.h, hwcontext_d3d11va.h) and the D3D11 API documentation.</summary>
public static unsafe class HwDecode
{
    // D3D11_BIND_FLAG (d3d11.h): the decoder writes the array, the renderer's shaders (or its copy) read it.
    private const uint BindShaderResource = 0x8;
    private const uint BindDecoder = 0x200;

    /// <summary>Surfaces the renderer may hold beyond the queue: two it reads, one being mapped.</summary>
    public const int RendererHeld = 3;

    /// <summary>A VAAPI device on the render node (DRM connection only; the caller has checked libva is usable), or 0 with words.</summary>
    internal static AVBufferRef* VaapiOnNode(string node, out string why)
    {
        why = "";
        AVBufferRef* device = null;
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "connection_type", "drm", 0);
        var r = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, node, options, 0);
        ffmpeg.av_dict_free(&options);
        if (r < 0)
        {
            why = $"vaapi on {node}: no device ({FfmpegUtil.ErrorText(r)})";
            return null;
        }
        return device;
    }

    /// <summary>A D3D11VA device on the share's ID3D11Device: AddRef'd for FFmpeg (which releases it with the device context),
    /// BindFlags DECODER | SHADER_RESOURCE, and lock callbacks that enter and exit the share's recursive lock (the device's immediate
    /// context is the renderer's too). 0 with words when the share has no device or FFmpeg refuses it.</summary>
    [SupportedOSPlatform("windows")]
    internal static AVBufferRef* D3d11vaOnShare(IGpuShare share, out string why)
    {
        why = "";
        var d3d = share.D3D11Device;
        if (d3d == 0)
        {
            why = "d3d11va: the renderer shares no D3D11 device";
            return null;
        }
        var buffer = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (buffer is null)
        {
            why = "d3d11va: FFmpeg could not allocate a device context";
            return null;
        }
        var device = (AVHWDeviceContext*)buffer->data;
        var hw = (AVD3D11VADeviceContext*)device->hwctx;
        AddRef(d3d);
        hw->device = (ID3D11Device*)d3d;
        hw->BindFlags = BindDecoder | BindShaderResource;
        var handle = GCHandle.Alloc(share);
        hw->lock_ctx = (void*)GCHandle.ToIntPtr(handle);
        hw->@lock = new AVD3D11VADeviceContext_lock_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, void>)&Lock };
        hw->unlock = new AVD3D11VADeviceContext_unlock_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, void>)&Unlock };
        // The handle lives as long as the device context: freed by its free callback, after FFmpeg's own uninit.
        device->user_opaque = (void*)GCHandle.ToIntPtr(handle);
        device->free = new AVHWDeviceContext_free_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<AVHWDeviceContext*, void>)&FreeHandle };
        var r = ffmpeg.av_hwdevice_ctx_init(buffer);
        if (r < 0)
        {
            why = $"d3d11va on the renderer's device: {FfmpegUtil.ErrorText(r)}";
            ffmpeg.av_buffer_unref(&buffer);
            return null;
        }
        return buffer;
    }

    // IUnknown::AddRef is the second slot of every COM vtable.
    [SupportedOSPlatform("windows")]
    private static void AddRef(nint unknown)
    {
        var vtable = *(nint**)unknown;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[1])(unknown);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Lock(void* ctx)
    {
        if (ctx is not null && GCHandle.FromIntPtr((nint)ctx).Target is IGpuShare share)
            share.Enter();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Unlock(void* ctx)
    {
        if (ctx is not null && GCHandle.FromIntPtr((nint)ctx).Target is IGpuShare share)
            share.Exit();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeHandle(AVHWDeviceContext* device)
    {
        if (device is not null && device->user_opaque is not null)
        {
            GCHandle.FromIntPtr((nint)device->user_opaque).Free();
            device->user_opaque = null;
        }
    }

    /// <summary>Maps a VAAPI frame to DRM PRIME into <paramref name="mapped"/> (read access: FFmpeg syncs the surface and exports it
    /// read-only, separate layers); the mapping holds the VAAPI frame until <paramref name="mapped"/> is unreferenced. FFmpeg's
    /// error code, 0 on success.</summary>
    internal static int MapToDrm(AVFrame* hardware, AVFrame* mapped)
    {
        ffmpeg.av_frame_unref(mapped);
        mapped->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
        return ffmpeg.av_hwframe_map(mapped, hardware, (int)AvHwframeMap.AV_HWFRAME_MAP_READ);
    }

    /// <summary>The surface of a mapped DRM PRIME frame (visible size from the frame); false with words when it does not import.</summary>
    internal static bool DrmSurface(AVFrame* mapped, GpuSurface surface, out string words) =>
        DrmFrames.ToSurface((AVDRMFrameDescriptor*)mapped->data[0], mapped->width, mapped->height, surface, out words);

    /// <summary>The surface of a D3D11 frame (data[0] the texture array, data[1] the slice; the coded size from its frames context)
    /// after the slot took its own reference to <paramref name="held"/>.</summary>
    internal static bool D3d11Surface(AVFrame* held, GpuSurface surface, out string words)
    {
        words = "";
        var frames = held->hw_frames_ctx is null ? null : (AVHWFramesContext*)held->hw_frames_ctx->data;
        if (held->data[0] is null || frames is null)
        {
            words = "the D3D11 frame has no texture";
            return false;
        }
        var format = frames->sw_format switch
        {
            AVPixelFormat.AV_PIX_FMT_NV12 => GpuSurfaceFormat.Nv12,
            AVPixelFormat.AV_PIX_FMT_P010LE => GpuSurfaceFormat.P010,
            _ => (GpuSurfaceFormat?)null,
        };
        if (format is null)
        {
            words = $"the decoder writes {ffmpeg.av_get_pix_fmt_name(frames->sw_format)}, not NV12 or P010";
            return false;
        }
        surface.Api = GpuApi.D3D11;
        surface.Format = format.Value;
        surface.Width = held->width;
        surface.Height = held->height;
        surface.CodedWidth = frames->width;
        surface.CodedHeight = frames->height;
        surface.Handle = (nint)held->data[0];
        surface.Slice = (int)(nint)held->data[1];
        surface.SurfaceId = ((long)(nint)held->data[0] << 8) ^ surface.Slice;
        surface.PlaneCount = 0;
        surface.Persistent = false;
        return true;
    }

    /// <summary>The frame bytes of a 4:2:0 picture in system memory (what one copy across the bus moves).</summary>
    public static long FrameBytes(int width, int height, bool tenBit) =>
        (long)width * height * 3 / 2 * (tenBit ? 2 : 1);
}
