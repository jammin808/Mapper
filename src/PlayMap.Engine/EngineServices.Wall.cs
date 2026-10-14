namespace PlayMap.Engine;

/// <summary>wp2-wall's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The calibration ledger align and stacks record their results in.</summary>
    public ICalibrationLedger? Ledger { get; init; }
}
