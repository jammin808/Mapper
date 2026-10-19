using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests;

/// <summary>Every kind in the catalogue draws at 320×180 with no GL error and no fault.</summary>
[Collection("gl")]
public class GeneratorSmokeTests(GlFixture gl)
{
    [Fact]
    public void EveryCatalogueKindRendersWithoutAFaultOrAGlError()
    {
        gl.Run(c =>
        {
            using var services = new RenderServices(c.Backend);
            using var host = new GeneratorHost(services);
            host.BeginFrame(1);
            foreach (var kind in GeneratorCatalog.All)
            {
                var g = new Generator("gn_" + kind.Id.Replace('.', '_'), kind.Name, kind.Id, ParamSet.Empty);
                var tex = host.Render(g, 320, 180, new GeneratorContext("Output 1", 1, "#FF8800", true), GeneratorFrame.At(12.34));
                Assert.Equal(320, tex.Width);
                Assert.Empty(host.Faults());
                Assert.Empty(c.Device.TakeErrors());
            }
        });
    }
}
