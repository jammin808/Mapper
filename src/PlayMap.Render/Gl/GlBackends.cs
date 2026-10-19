using PlayMap.Core.Gpu;

namespace PlayMap.Render.Gl;

/// <summary>The GL device under a GL backend, for the frame code that draws on the GL directly beside the frame's list (both are
/// immediate on the one context, in order, through one state cache). A device of another backend has none and is refused.</summary>
internal static class GlBackends
{
    extension(IGpuDevice device)
    {
        /// <summary>The backend's GL device.</summary>
        internal GlDevice Gl => device is GlBackend gl
            ? gl.Device
            : throw new NotSupportedException($"the {device.Kind} device draws through its command lists only: it has no GL device");
    }
}
