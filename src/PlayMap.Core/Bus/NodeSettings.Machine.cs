using PlayMap.Core.Machine;

namespace PlayMap.Core.Bus;

/// <summary>wp3-platform's machine-local settings: MACHINE SET's claims and MACHINE LOCK's driver set (this node's own, never the
/// project's); an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public MachineSettings Machine { get; init; } = MachineSettings.Default;
}
