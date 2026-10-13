namespace PlayMap.Engine.Host;

/// <summary>The operations side's parts in the exe's composition (<see cref="Standard"/>): the scan runner is the scan
/// backend, and projector control, the assistant and the scan's binding are its extensions (<see cref="OpsParts"/>).</summary>
public sealed partial record EngineParts
{
    static partial void UseScanBackend(ref Func<ScanBackendRequest, IScanBackend>? factory) => factory = OpsParts.ScanBackend();

    static partial void AddExtensions(List<EngineExtensionFactory> extensions) => extensions.AddRange(OpsParts.Extensions());
}
