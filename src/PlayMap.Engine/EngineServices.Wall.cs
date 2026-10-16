using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-wall's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The calibration ledger align and stacks record their results in: a late ledger the first engine built from these
    /// services binds to itself (its CalibrationLedgerService).</summary>
    public ICalibrationLedger? Ledger { get; init; } = new Wall.LateCalibrationLedger();

    /// <summary>Where a camera re-solve puts its dots (the render host); null: only a simulated camera re-solves.</summary>
    public IScanDisplay? WallDisplay { get; init; }

    /// <summary>Opens a real camera for a camera re-solve (null: the camera its rig is — a phone through the node's phone hub, else a capture device or a stream).</summary>
    public Func<CameraRig, Scanning.IScanCamera?>? WallCamera { get; init; }
}
