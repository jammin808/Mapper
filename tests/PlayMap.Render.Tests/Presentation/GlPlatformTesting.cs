using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The GL context under a GL platform, for the tests that hold the GL backend's own platforms to their contexts: a platform
/// of another backend has none, and the cast refuses it.</summary>
internal static class GlPlatformTesting
{
    extension(IGpuPlatform platform)
    {
        public RenderContext Context => ((IGlPlatform)platform).Context;
    }
}
