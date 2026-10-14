namespace PlayMap.Engine.Host;

/// <summary>wp2-control's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddControlParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-control fills this.
    }
}
