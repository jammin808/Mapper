using PlayMap.Vision.Onnx;

namespace PlayMap.Vision.Tests;

/// <summary>The session opens on the best provider the machine has and falls back to the CPU without throwing
/// when a GPU provider is missing or fails; inputs and outputs are discovered from the model and fed in their
/// declared types; a cancelled run throws.</summary>
public class OnnxSessionTests
{
    /// <summary>y = x + float(k): one float input, one int64 input, one float output.</summary>
    internal static byte[] AddModel() => new OnnxModelBuilder()
        .Input("x", 1, 4)
        .InputInt64("k", 1)
        .Output("y", 1, 4)
        .Node("Cast", ["k"], ["kf"], ("to", 1))
        .Node("Add", ["x", "kf"], ["y"])
        .Build();

    [Fact]
    public void AMissingOrFailingGpuProviderFallsBackToTheCpu()
    {
        var available = new HashSet<string>(["CUDAExecutionProvider", "CPUExecutionProvider"]);
        var tried = new List<ExecutionProvider>();
        var (session, attempts) = OnnxSession.OpenWithFallback(
            OnnxSessionSettings.Default.Order(), available, p =>
            {
                tried.Add(p);
                return p == ExecutionProvider.Cuda ? throw new InvalidOperationException("no CUDA device") : "cpu-session";
            });
        Assert.Equal("cpu-session", session);
        Assert.Equal([ExecutionProvider.Cuda, ExecutionProvider.Cpu], tried);
        Assert.Equal(3, attempts.Count);
        Assert.False(attempts[0].Opened);
        Assert.Contains("no CUDA device", attempts[0].Words, StringComparison.Ordinal);
        Assert.Equal(ExecutionProvider.DirectML, attempts[1].Provider);
        Assert.Equal("not in this runtime build", attempts[1].Words);
        Assert.True(attempts[2].Opened);
    }

    [Fact]
    public void TheCpuIsTriedLastEvenWhenThePreferenceForgetsIt()
    {
        var settings = new OnnxSessionSettings([ExecutionProvider.DirectML]);
        Assert.Equal([ExecutionProvider.DirectML, ExecutionProvider.Cpu], settings.Order().ToArray());
    }

    [Fact]
    public void OpeningThrowsOnlyWhenTheCpuFailsTooWithEveryAttemptsWords()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OnnxSession.OpenWithFallback<string>(
            OnnxSessionSettings.Default.Order(), new HashSet<string>(), _ => throw new FormatException("bad model")));
        Assert.Contains("Cuda: not in this runtime build", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Cpu: bad model", ex.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public async Task ARealSessionOpensOnTheCpuHereAndRunsWithDiscoveredNames()
    {
        using var session = OnnxSession.Open(AddModel(), OnnxSessionSettings.Default);
        // The base package carries only the CPU provider; the GPU ones are reported as absent, not thrown.
        Assert.Equal(ExecutionProvider.Cpu, session.Provider);
        Assert.Contains(session.Attempts, a => a.Provider == ExecutionProvider.Cuda && !a.Opened);
        Assert.Equal(["k", "x"], session.Inputs.Select(i => i.Name).Order());
        Assert.Equal([1L, 4L], session.Input("x")!.Dimensions.ToArray());
        Assert.Equal(Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Int64, session.Input("k")!.ElementType);
        Assert.True(session.HasOutput("y"));

        using var x = new TensorBuffer("x", 1, 4);
        x.Span[0] = 1;
        x.Span[3] = -2;
        using var k = TensorBuffer.Scalar("k", 5, 1);
        var outputs = await session.RunAsync([x, k], ["y"], CancellationToken.None);
        Assert.Equal([6f, 5f, 5f, 3f], outputs["y"].Values);
        Assert.Equal([1L, 4L], outputs["y"].Shape.ToArray());
    }

    [Fact]
    public async Task ACancelledRunThrows()
    {
        using var session = OnnxSession.Open(AddModel(), OnnxSessionSettings.CpuOnly);
        using var x = new TensorBuffer("x", 1, 4);
        using var k = TensorBuffer.Scalar("k", 1, 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync([x, k], ["y"], cts.Token));
    }
}
