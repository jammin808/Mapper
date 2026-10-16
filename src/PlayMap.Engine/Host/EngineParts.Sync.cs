using PlayMap.Engine.Sync;

namespace PlayMap.Engine.Host;

/// <summary>wp2-sync's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddSyncParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(SyncExtension.Factory);
    }
}
