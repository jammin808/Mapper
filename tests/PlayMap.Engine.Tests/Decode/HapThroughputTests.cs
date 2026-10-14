using System.Text.Json;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Decode;

/// <summary>This node's 4K60 HAP layer count, measured (the second stage of a synthetic 4K HAP Q frame on one core, upload not
/// part of it) and recorded in STATE.decode.platform.hapLayers4k60. A measurement, never a pass/fail ceiling: the integration
/// gate runs it serially and keeps the number.</summary>
[Trait("Category", "Perf")]
public sealed class HapThroughputTests(ITestOutputHelper log)
{
    [Fact]
    public async Task TheLayerCountIsMeasuredAndRecordedInState()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        using (var before = JsonDocument.Parse(engine.State().Json))
            Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("decode").GetProperty("platform").GetProperty("hapLayers4k60").ValueKind);

        var measured = await engine.DecodeFacts.EnsureMeasuredAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        log.WriteLine($"4K60 HAP Q layers: {measured.Value} ({measured.Limit}) — {measured.Words}");
        Assert.Equal("cores", measured.Limit);
        Assert.Equal(Environment.ProcessorCount, measured.Cores);
        Assert.True(measured.DecodeMs > 0);
        Assert.Equal((int)Math.Floor(measured.Cores * (1000.0 / 60) / measured.DecodeMs), measured.Value);
        // A second caller shares the same measurement.
        Assert.Same(measured, await engine.DecodeFacts.EnsureMeasuredAsync(CancellationToken.None));

        using var state = JsonDocument.Parse(engine.State().Json);
        var layers = state.RootElement.GetProperty("decode").GetProperty("platform").GetProperty("hapLayers4k60");
        Assert.Equal(measured.Value, layers.GetProperty("value").GetInt32());
        Assert.Equal("cores", layers.GetProperty("limit").GetString());
        Assert.Equal(measured.MeasuredAtUnixMs, layers.GetProperty("measuredAt").GetInt64());
    }
}
