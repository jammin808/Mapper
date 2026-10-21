using BCnEncoder.Shared;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Gl;
using PlayMap.Render.Video;

namespace PlayMap.Render.Tests.Hap;

/// <summary>HAP's GL facts: the window-system binding a decoder hands its frames through is the one the current context came
/// through, and a context lacking a block family refuses it with words, never a black frame.</summary>
[Collection("gpu")]
public class HapGlTests(BackendFixture backends)
{
    [Fact]
    public void TheBindingIsWhatTheCurrentContextCameThroughNotTheEnvironment()
    {
        // The fixture's context is GLFW's on an X server, made through GLX for desktop GL and through EGL for the ES leg's (GLX when
        // EGL refused the window): the binding is the one it was made through. A thread with no context current gets no answer.
        var (bound, made) = backends.RunGl(c => (GlDevice.CurrentBinding(), c.Api));
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            Assert.Equal(made.StartsWith("egl", StringComparison.Ordinal) ? "egl" : "glx", bound);
        else
            Assert.Contains(bound, new[] { "egl", "glx", "wgl", "cgl" });
        Assert.Equal("", GlDevice.CurrentBinding());
    }

    [Fact]
    public void AContextLackingAFamilyRefusesWithWordsNeverABlackFrame()
    {
        var blocks = HapRenderTests.Encode(CompressionFormat.Bc7, HapRenderTests.Image(8, 8, 3), 8, 8);
        using var lease = new ArrayLease(PixelLayout.Bc7, 8, 8, 1, ColorMatrix.Bt709, ColorRange.Full, (blocks, 2 * 16, 2, 2));
        var (words, refusal, picture) = backends.RunGl(c =>
        {
            using var set = new VideoTextureSet(c.Backend);
            // An ES 3.0 context with S3TC and RGTC and no BPTC (an ANGLE-like list).
            set.Features = GlBackend.WithCaps(c.Backend.Features,
                GlCaps.From("es300", 3, 0, new HashSet<string> { "GL_EXT_texture_compression_s3tc", "GL_EXT_texture_compression_rgtc" }));
            var e = Assert.Throws<NotSupportedException>(() => set.Update(lease));
            return (e.Message, set.Refusal, set.Picture);
        });
        Assert.Equal("this GPU's GL (es300) does not offer BPTC textures, which Bc7 needs", words);
        Assert.Equal(words, refusal);
        Assert.Null(picture);

        // On llvmpipe's own core context every family is there.
        var formats = backends.RunGl(c => c.Device.BlockFormats);
        Assert.Equal(BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4 | BlockFormats.Bc7 | BlockFormats.Bc6h, formats);
        Assert.Null(backends.RunGl(c => c.Device.RefuseBlockFormat(TextureFormat.Bc6hSfloat)));
    }
}
