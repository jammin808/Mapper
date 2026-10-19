using System.Text.Json;

namespace PlayMap.Engine;

/// <summary>wp3-installer's hooks into the engine (EngineCore.Modules.cs): the machine block's install key, from the install's
/// registry key and the supervisor's status file. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void MachineInstall(Utf8JsonWriter w)
    {
        // wp3-installer fills this.
    }
}
