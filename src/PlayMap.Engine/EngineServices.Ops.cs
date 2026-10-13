using PlayMap.Engine.Assistant;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine;

/// <summary>The operations services: projector control and the node's assistant. Built before the engine (they
/// hold no engine), attached once it exists (<see cref="OpsServices.Attach"/>); null leaves the verbs that need
/// them refusing with the reason.</summary>
public sealed partial record EngineServices
{
    /// <summary>Projector control: the device manager, the PJLink socket, discovery and the credential store.</summary>
    public DeviceService? Devices { get; init; }

    /// <summary>The node's assistant: one approval queue per node, one conversation per client.</summary>
    public AssistantService? Assistant { get; init; }
}
