using PlayMap.Engine.Machine;

namespace PlayMap.Engine;

/// <summary>wp3-platform's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>This node's machine settings and the runtimes their owners fill (wp3-d3d12-core the Agility runtime, Media FFmpeg's
    /// build, wp3-framelock the sync board's firmware); null on a node with no node home, where MACHINE SET and MACHINE LOCK say so.</summary>
    public MachineNode? MachineNode { get; init; }
}
