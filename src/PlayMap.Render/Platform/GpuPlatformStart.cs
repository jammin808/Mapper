namespace PlayMap.Render.Platform;

/// <summary>The stages the DXGI flip platform comes up through, in order; a start that stops names the stage it stopped at.</summary>
public enum DxgiStartStage
{
    /// <summary>The DXGI factory and the adapter that drives the first display output.</summary>
    Adapter,
    /// <summary>The Direct3D 11 device on it.</summary>
    Device,
    /// <summary>ANGLE's library loaded by name and its EGL entry points resolved.</summary>
    Angle,
    /// <summary>ANGLE's client, device and display extensions present.</summary>
    Extensions,
    /// <summary>The EGL display on our device, its ES 3.0 context made current.</summary>
    Context,
    /// <summary>The GL device on that context and every shader program translated.</summary>
    Programs,
}

/// <summary>A platform start that stopped at a stage, with the words why.</summary>
public sealed class GpuPlatformStartException : Exception
{
    public GpuPlatformStartException(DxgiStartStage stage, string words)
        : base(words)
    {
        Stage = stage;
    }

    public GpuPlatformStartException()
    {
    }

    public GpuPlatformStartException(string message)
        : base(message)
    {
    }

    public GpuPlatformStartException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public DxgiStartStage Stage { get; }

    public static string Word(DxgiStartStage stage) => stage switch
    {
        DxgiStartStage.Adapter => "adapter",
        DxgiStartStage.Device => "device",
        DxgiStartStage.Angle => "angle",
        DxgiStartStage.Extensions => "extensions",
        DxgiStartStage.Context => "context",
        _ => "programs",
    };
}
