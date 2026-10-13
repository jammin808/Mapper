namespace PlayMap.Core.Model;

/// <summary>
/// A projector box PlayMap controls over the venue's network, kept with the show like the projector it is: where
/// it answers, which protocol it speaks, which secret it needs (by reference only) and which PlayMap projector and
/// output it is. It lives in the project because it names the project's own projector and output, it is edited
/// and undone like any other entity, and a backup node opening the same show controls the same boxes. Nothing
/// here is a secret: <see cref="CredentialRef"/> names an entry in the node's own credential store, so a link
/// travels in the file, on the wire and in the journal whole, and a show copied to another node needs that
/// node's own entry before a protected projector answers.
/// </summary>
/// <param name="Id">"dv" and eight hex digits (<see cref="Ids.Device"/>).</param>
/// <param name="Protocol">The control protocol's word: "pjlink", "christie", "panasonic", "barco-pulse",
/// "sony-adcp", "epson" or "nec" (the engine refuses any other).</param>
/// <param name="Host">The projector's address or name on the venue's network.</param>
/// <param name="Port">0 = the protocol's default port.</param>
/// <param name="CredentialRef">The key of this link's secret in the node's credential store; "" = no password.</param>
/// <param name="ProjectorId">The PlayMap projector (pose and optics) this box is; "" = not linked yet.</param>
/// <param name="OutputId">The PlayMap output that feeds it; "" = not linked yet.</param>
/// <param name="PollIntervalMs">0 = the default (10 s); never below 1 s.</param>
/// <param name="AllowUnverified">The operator tested this model and allows the driver's commands no public
/// document confirmed. Off unless an operator turns it on.</param>
public sealed record DeviceLink(
    string Id,
    string Name,
    string Protocol,
    string Host,
    int Port,
    string CredentialRef,
    string ProjectorId,
    string OutputId,
    int PollIntervalMs,
    bool AllowUnverified) : IEntity
{
    /// <summary>The protocol every venue projector understands (power, input, shutter, health).</summary>
    public const string DefaultProtocol = "pjlink";

    /// <summary>The projector's own processing as read before PlayMap reset it (wp2-align).</summary>
    public NativeSnapshot? Snapshot { get; init; }
}
