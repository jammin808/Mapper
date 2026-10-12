using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Drivers;

/// <summary>Where a driver's commands come from — the honesty the desk shows beside the link.</summary>
public enum DriverProvenance
{
    /// <summary>Every command is in a vendor or standards document (PJLink's JBMIA spec, Panasonic's tables).</summary>
    VendorDocument,

    /// <summary>The commands come from public implementations used against real projectors, built from the
    /// vendor's manual; the manual itself was not read (it is behind the vendor's site).</summary>
    PublicImplementation,

    /// <summary>Recalled from the vendor's manual and not re-verified against a public source: every command is
    /// gated behind <see cref="ProjectorLink.AllowUnverified"/>.</summary>
    Unverified,
}

/// <summary>What a status or lens read found: the outcome and the facts (null = nothing read).</summary>
public sealed record DriverReading(CommandResult Result, DeviceState? State)
{
    /// <summary>The projector answered — even a "no" proves it is there.</summary>
    public bool Reached => Result.Stage is CommandStage.Rejected or CommandStage.Accepted or CommandStage.Observed;
}

/// <summary>
/// One projector, through one protocol. Every command returns how far it got (<see cref="CommandResult"/>); a
/// command the protocol has no published word for is refused with the reason, never approximated. A driver holds
/// no connection between calls unless its protocol needs one; the manager runs one call at a time per projector.
/// </summary>
public interface IProjectorDriver : IAsyncDisposable
{
    ProjectorLink Link { get; }

    ProjectorProtocol Protocol { get; }

    DriverProvenance Provenance { get; }

    /// <summary>Everything this driver can send.</summary>
    ProjectorCapabilities Capabilities { get; }

    /// <summary>The subset no public document confirmed for every model: refused unless the link allows it.</summary>
    ProjectorCapabilities Unverified { get; }

    /// <summary>Why a capability is gated or model-dependent ("documented for PT-DZ21K2 series…"); "" for none.</summary>
    string Caveat(ProjectorCapabilities capability);

    /// <summary>The pattern names <see cref="ShowTestPatternAsync"/> takes (besides "off" and "identify").</summary>
    IReadOnlyList<string> TestPatternNames { get; }

    /// <summary>The driver can show a built-in pattern to identify the projector ("identify").</summary>
    bool CanIdentify { get; }

    /// <summary>Opens a connection and completes the handshake (authentication included) without changing anything.</summary>
    Task<CommandResult> ConnectAsync(CancellationToken ct);

    Task<CommandResult> SetPowerAsync(bool on, CancellationToken ct);

    /// <summary>An input in the protocol's own words ("31" for PJLink, "HD1" for Panasonic, "L1 HDMI" for Pulse).</summary>
    Task<CommandResult> SelectInputAsync(string input, CancellationToken ct);

    Task<CommandResult> SetShutterAsync(bool closed, CancellationToken ct);

    Task<CommandResult> SetFreezeAsync(bool frozen, CancellationToken ct);

    Task<CommandResult> MoveLensAsync(LensMove move, CancellationToken ct);

    /// <summary>Recalls lens memory <paramref name="slot"/> (1-based, as the projector's menu counts).</summary>
    Task<CommandResult> RecallLensMemoryAsync(int slot, CancellationToken ct);

    /// <summary>Shows a built-in pattern by name (<see cref="TestPatternNames"/>, "identify"), or "off".</summary>
    Task<CommandResult> ShowTestPatternAsync(string pattern, CancellationToken ct);

    /// <summary>Switches the projector's own geometry correction (keystone/warp) off.</summary>
    Task<CommandResult> ResetGeometryAsync(CancellationToken ct);

    Task<CommandResult> UploadGeometryAsync(WarpGrid grid, CancellationToken ct);

    Task<CommandResult> SetEdgeBlendAsync(EdgeBlendSettings blend, CancellationToken ct);

    Task<CommandResult> SetBlackLevelAsync(BlackLevelSettings level, CancellationToken ct);

    Task<CommandResult> SetColorGainsAsync(ColorGains gains, CancellationToken ct);

    /// <summary>Reads health and identity: power, shutter, input, faults, hours, temperatures, model, signal.</summary>
    Task<DriverReading> ReadStatusAsync(CancellationToken ct);

    /// <summary>Reads lens motor positions in raw units.</summary>
    Task<DriverReading> ReadLensAsync(CancellationToken ct);

    /// <summary>An operator's line in the protocol's own grammar, sent as typed (framing and authentication
    /// added); the projector's answer comes back in <see cref="CommandResult.Reply"/>.</summary>
    Task<CommandResult> RawAsync(string line, CancellationToken ct);
}
