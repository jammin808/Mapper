namespace PlayMap.Engine.Host;

/// <summary>wp3-d3d12-passes's parts in the exe's composition (EngineParts.Standard), added in the spine's order: its arm hook (plans compiled at arm; wp3-d3d12-passes-2 continues it), set in its
/// extension's Configure.</summary>
public sealed partial record EngineParts
{
    static partial void AddPlanParts(List<EngineExtensionFactory> extensions)
    {
        // wp3-d3d12-passes fills this.
    }
}
