namespace PlayMap.Engine.Host;

/// <summary>The render side's part of the exe: the node's render host is <see cref="Rendering.RenderHost.ForNode"/>.</summary>
public sealed partial record EngineParts
{
    static partial void UseRenderHost(ref Func<RenderHostRequest, IRenderHost?>? factory) => factory = Rendering.RenderHost.ForNode;
}
