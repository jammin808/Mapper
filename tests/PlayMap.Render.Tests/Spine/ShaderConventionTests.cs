using PlayMap.Render.Gpu;
using PlayMap.Render.Shaders;

namespace PlayMap.Render.Tests.Spine;

/// <summary>Program sources are dialect-free: none carries a #version or a precision line, the device refuses one that does (with
/// words), and every program compiles and links on the tests' core 3.3 context with the header the device writes.</summary>
[Collection("gl")]
public sealed class ShaderConventionTests(GlFixture gl)
{
    private static bool HoldsHeader(string source) =>
        source.Contains("#version", StringComparison.Ordinal)
        || source.Split('\n').Any(l => l.TrimStart().StartsWith("precision ", StringComparison.Ordinal));

    [Fact]
    public void NoProgramSourceHoldsAVersionOrAPrecisionLine()
    {
        Assert.NotEmpty(ShaderLibrary.Names);
        foreach (var name in ShaderLibrary.Names)
        {
            var (vs, fs) = ShaderLibrary.Source(name);
            Assert.False(HoldsHeader(vs), name + " vertex");
            Assert.False(HoldsHeader(fs), name + " fragment");
        }
        Assert.Equal("#version 330 core\n", GlDialects.Header(GlDialect.Core330));
    }

    [Fact]
    public void TheDeviceRefusesASourceThatBringsItsOwnHeader()
    {
        const string Vs = "void main() { gl_Position = vec4(0.0); }\n";
        const string Fs = "out vec4 oColor;\nvoid main() { oColor = vec4(1.0); }\n";
        gl.Run(c =>
        {
            var version = Assert.Throws<ArgumentException>(() => c.Device.CreateProgram("t1", "#version 330 core\n" + Vs, Fs));
            Assert.Contains("own #version", version.Message, StringComparison.Ordinal);
            // Behind a comment it is still the source's own header, refused with the same words rather than a compile error.
            var behind = Assert.Throws<ArgumentException>(() => c.Device.CreateProgram("t4", Vs, "// an effect\n# version 300 es\n" + Fs));
            Assert.Contains("own #version", behind.Message, StringComparison.Ordinal);
            var precision = Assert.Throws<ArgumentException>(() => c.Device.CreateProgram("t2", Vs, "precision highp float;\n" + Fs));
            Assert.Contains("precision statement", precision.Message, StringComparison.Ordinal);
            using var ok = c.Device.CreateProgram("t3", Vs, Fs);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void EveryProgramCompilesAndLinksOnCore33()
    {
        gl.Run(c =>
        {
            Assert.Equal(GlDialect.Core330, c.Device.Dialect);
            foreach (var name in ShaderLibrary.Names)
                Assert.NotNull(c.Device.Program(name));
            Assert.Empty(c.Device.TakeErrors());
        });
    }
}
