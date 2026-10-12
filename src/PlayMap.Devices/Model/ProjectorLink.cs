using PlayMap.Core.Model;

namespace PlayMap.Devices.Model;

/// <summary>The control protocol a projector link speaks. Each has its own driver; PJLink is the fallback every
/// venue projector understands (power, input, shutter, health).</summary>
public enum ProjectorProtocol
{
    /// <summary>PJLink class 1 and 2 (JBMIA), TCP 4352; discovery and notifications on UDP 4352.</summary>
    PjLink,

    /// <summary>Christie's ASCII serial protocol carried over TCP 3002: "(PWR 1)", "(SHU?)" → "(SHU!001)".</summary>
    ChristieAscii,

    /// <summary>Panasonic NTCONTROL, TCP 1024: "NTCONTROL 1 seed" then MD5-protected "00POF" lines.</summary>
    PanasonicNtControl,

    /// <summary>Barco's Pulse platform JSON-RPC 2.0 over TCP 9090.</summary>
    BarcoPulse,

    /// <summary>Sony ADCP, TCP 53595, SHA-256 challenge. The driver is unverified (see docs/PROJECTORS.md).</summary>
    SonyAdcp,

    /// <summary>Epson ESC/VP.net (TCP 3629, a 16-byte CONNECT handshake) carrying ESC/VP21 lines.</summary>
    EpsonEscVpNet,

    /// <summary>NEC / Sharp NEC binary control, TCP 7142, checksummed frames. The driver is unverified.</summary>
    NecBinary,
}

/// <summary>
/// One projector PlayMap talks to: where it is, which protocol, which secret (by reference) and which PlayMap
/// projector and output it is. The integrator adds a list of these to the project model; nothing here holds a
/// secret — <see cref="CredentialRef"/> names an entry in an <see cref="ICredentialStore"/>, so a link can be
/// logged, journaled and sent over the wire whole.
/// </summary>
/// <param name="Id">A project id (<see cref="Ids"/>-shaped, e.g. "dv1a2b3c4d").</param>
/// <param name="Port">0 = the protocol's default (<see cref="ProjectorProtocols.DefaultPort"/>).</param>
/// <param name="CredentialRef">The key of this link's credential in the store; "" = no password set.</param>
/// <param name="ProjectorId">The PlayMap projector (pose and optics) this box is; "" = not linked yet.</param>
/// <param name="OutputId">The PlayMap output that feeds it; "" = not linked yet.</param>
/// <param name="PollIntervalMs">0 = the default (10 s). Never below 1 s: a projector's controller is slow.</param>
/// <param name="AllowUnverified">The operator tested this model and allows the driver's commands that no
/// public document confirmed (each driver lists them). Off by default; such commands are refused with the reason.</param>
public sealed record ProjectorLink(
    string Id,
    string Name,
    ProjectorProtocol Protocol,
    string Host,
    int Port = 0,
    string CredentialRef = "",
    string ProjectorId = "",
    string OutputId = "",
    int PollIntervalMs = 0,
    bool AllowUnverified = false) : IEntity
{
    /// <summary>The id prefix for device links ("dv").</summary>
    public const string IdPrefix = "dv";

    public const int DefaultPollMs = 10_000;
    public const int MinPollMs = 1_000;

    public int EffectivePort => Port > 0 ? Port : ProjectorProtocols.DefaultPort(Protocol);

    public int EffectivePollMs => PollIntervalMs <= 0 ? DefaultPollMs : Math.Max(MinPollMs, PollIntervalMs);
}

/// <summary>A user name and secret for a projector. Never printed: <see cref="ToString"/> says "credential" and
/// nothing else, so a credential that reaches a log line or an exception message by accident leaks nothing.</summary>
public sealed class Credential
{
    public Credential(string user, string secret)
    {
        User = user ?? "";
        Secret = secret ?? "";
    }

    /// <summary>The user name where the protocol has one (Panasonic's "admin1"); "" otherwise.</summary>
    public string User { get; }

    /// <summary>The password. Only a driver's handshake reads it.</summary>
    public string Secret { get; }

    public override string ToString() => "credential";
}

/// <summary>Where secrets live (the engine's secret store). A missing reference is "no password".</summary>
public interface ICredentialStore
{
    Credential? Find(string reference);
}

/// <summary>A store in memory, for tests and for a desk that holds the secrets itself.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, Credential> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Set(string reference, Credential credential)
    {
        lock (_gate)
            _items[reference] = credential;
    }

    public Credential? Find(string reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;
        lock (_gate)
            return _items.GetValueOrDefault(reference);
    }

    public override string ToString() => "credential store";
}

/// <summary>Per-protocol facts: default ports and names, from each vendor's published documentation.</summary>
public static class ProjectorProtocols
{
    public const int PjLinkPort = 4352;
    public const int ChristiePort = 3002;
    public const int PanasonicPort = 1024;
    public const int BarcoPulsePort = 9090;
    public const int SonyAdcpPort = 53595;
    public const int EpsonEscVpNetPort = 3629;
    public const int NecPort = 7142;

    public static int DefaultPort(ProjectorProtocol p) => p switch
    {
        ProjectorProtocol.PjLink => PjLinkPort,
        ProjectorProtocol.ChristieAscii => ChristiePort,
        ProjectorProtocol.PanasonicNtControl => PanasonicPort,
        ProjectorProtocol.BarcoPulse => BarcoPulsePort,
        ProjectorProtocol.SonyAdcp => SonyAdcpPort,
        ProjectorProtocol.EpsonEscVpNet => EpsonEscVpNetPort,
        ProjectorProtocol.NecBinary => NecPort,
        _ => PjLinkPort,
    };

    /// <summary>The protocol's name for the desk, the wire and the journal.</summary>
    public static string Label(ProjectorProtocol p) => p switch
    {
        ProjectorProtocol.PjLink => "PJLink",
        ProjectorProtocol.ChristieAscii => "Christie serial (TCP)",
        ProjectorProtocol.PanasonicNtControl => "Panasonic NTCONTROL",
        ProjectorProtocol.BarcoPulse => "Barco Pulse (JSON-RPC)",
        ProjectorProtocol.SonyAdcp => "Sony ADCP",
        ProjectorProtocol.EpsonEscVpNet => "Epson ESC/VP.net",
        ProjectorProtocol.NecBinary => "NEC binary",
        _ => p.ToString(),
    };

    /// <summary>The word for the wire ("pjlink", "christie"…) and back; unknown words are refused (null).</summary>
    public static string Word(ProjectorProtocol p) => p switch
    {
        ProjectorProtocol.PjLink => "pjlink",
        ProjectorProtocol.ChristieAscii => "christie",
        ProjectorProtocol.PanasonicNtControl => "panasonic",
        ProjectorProtocol.BarcoPulse => "barco-pulse",
        ProjectorProtocol.SonyAdcp => "sony-adcp",
        ProjectorProtocol.EpsonEscVpNet => "epson",
        ProjectorProtocol.NecBinary => "nec",
        _ => "pjlink",
    };

    public static ProjectorProtocol? Parse(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "pjlink" => ProjectorProtocol.PjLink,
        "christie" => ProjectorProtocol.ChristieAscii,
        "panasonic" => ProjectorProtocol.PanasonicNtControl,
        "barco-pulse" or "barco" => ProjectorProtocol.BarcoPulse,
        "sony-adcp" or "sony" => ProjectorProtocol.SonyAdcp,
        "epson" => ProjectorProtocol.EpsonEscVpNet,
        "nec" => ProjectorProtocol.NecBinary,
        _ => null,
    };
}
