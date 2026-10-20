using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;

namespace PlayMap.Render.Tests;

/// <summary>Every kind in the catalogue draws at 320×180 with no device error and no fault.</summary>
[Collection("gpu")]
public class GeneratorSmokeTests(BackendFixture backends)
{
    [Fact]
    public void EveryCatalogueKindRendersWithoutAFaultOrADeviceError()
    {
        backends.Run(leg =>
        {
            using var services = new RenderServices(leg.Device);
            using var host = new GeneratorHost(services);
            host.BeginFrame(1);
            foreach (var kind in GeneratorCatalog.All)
            {
                var g = new Generator("gn_" + kind.Id.Replace('.', '_'), kind.Name, kind.Id, ParamSet.Empty);
                var tex = host.Render(g, 320, 180, new GeneratorContext("Output 1", 1, "#FF8800", true), GeneratorFrame.At(12.34));
                Assert.Equal(320, tex.Width);
                Assert.Empty(host.Faults());
                Assert.Empty(leg.TakeErrors());
            }
        });
    }
}
