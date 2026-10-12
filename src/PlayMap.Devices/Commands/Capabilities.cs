namespace PlayMap.Devices.Commands;

/// <summary>What a driver can do through its protocol. A flag is set only where a public document (or a public
/// implementation built from one) gives the command; the driver lists which flags are gated as unverified.</summary>
[Flags]
public enum ProjectorCapabilities
{
    None = 0,
    Power = 1 << 0,
    Input = 1 << 1,
    /// <summary>Shutter or picture mute (AV mute): the light leaving the lens stops.</summary>
    Shutter = 1 << 2,
    Freeze = 1 << 3,
    /// <summary>Lens motors can be driven (absolute or by steps — see <see cref="LensMove"/>).</summary>
    LensMotor = 1 << 4,
    /// <summary>Lens motor positions can be read back.</summary>
    LensPositionRead = 1 << 5,
    LensMemory = 1 << 6,
    TestPatterns = 1 << 7,
    /// <summary>The projector's own geometry correction (keystone/warp) can be switched off or reset.</summary>
    GeometryReset = 1 << 8,
    /// <summary>A warp grid can be written into the projector.</summary>
    GeometryUpload = 1 << 9,
    EdgeBlend = 1 << 10,
    BlackLevel = 1 << 11,
    ColorGains = 1 << 12,
    /// <summary>Health and identity can be read (power, faults, hours, model…).</summary>
    Status = 1 << 13,
}

public static class CapabilityWords
{
    /// <summary>Every single flag, in declaration order.</summary>
    public static readonly ProjectorCapabilities[] All = Enum.GetValues<ProjectorCapabilities>()
        .Where(c => c != ProjectorCapabilities.None).ToArray();

    public static string Word(ProjectorCapabilities c) => c switch
    {
        ProjectorCapabilities.Power => "power",
        ProjectorCapabilities.Input => "input",
        ProjectorCapabilities.Shutter => "shutter",
        ProjectorCapabilities.Freeze => "freeze",
        ProjectorCapabilities.LensMotor => "lens motors",
        ProjectorCapabilities.LensPositionRead => "lens position read",
        ProjectorCapabilities.LensMemory => "lens memory",
        ProjectorCapabilities.TestPatterns => "test patterns",
        ProjectorCapabilities.GeometryReset => "geometry reset",
        ProjectorCapabilities.GeometryUpload => "geometry upload",
        ProjectorCapabilities.EdgeBlend => "edge blend",
        ProjectorCapabilities.BlackLevel => "black level",
        ProjectorCapabilities.ColorGains => "colour gains",
        ProjectorCapabilities.Status => "status",
        _ => string.Join(", ", All.Where(f => (c & f) != 0).Select(Word)),
    };

    /// <summary>"power, shutter, status" for a set of flags; "none" for none.</summary>
    public static string List(ProjectorCapabilities c) =>
        c == ProjectorCapabilities.None ? "none" : string.Join(", ", All.Where(f => (c & f) != 0).Select(Word));
}
