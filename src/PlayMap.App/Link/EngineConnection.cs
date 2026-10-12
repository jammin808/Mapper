using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

public enum ConnectChoice
{
    /// <summary>This machine's own engine: started (and watched) by the desk, reached on loopback.</summary>
    OwnEngine,
    /// <summary>A node on the network: host:port and its pairing token.</summary>
    Network,
    /// <summary>The demo engine inside the desk: no outputs, every panel clickable.</summary>
    Demo,
}

/// <summary>Where an engine is: a host, the wire port and, across the network, the pairing token (which never
/// rides a line — the link answers the node's NONCE with a proof).</summary>
public sealed record EngineEndpoint(string Host, int Port, string? Token, bool Local)
{
    public static EngineEndpoint Loopback => new("127.0.0.1", ShowBusConstants.PlayMapWirePort, null, true);

    /// <summary>"10.0.0.7", "10.0.0.7:9941", "stage-left.local:9941" → an endpoint; false when unreadable.</summary>
    public static bool TryParse(string? text, string? token, out EngineEndpoint endpoint)
    {
        endpoint = Loopback;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        var port = ShowBusConstants.PlayMapWirePort;
        var colon = t.LastIndexOf(':');
        if (colon > 0 && t.IndexOf(':', StringComparison.Ordinal) == colon)
        {
            if (!int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                return false;
            t = t[..colon];
        }
        if (t.Length == 0 || t.Any(char.IsWhiteSpace))
            return false;
        endpoint = new EngineEndpoint(t, port, string.IsNullOrWhiteSpace(token) ? null : token.Trim(), t is "127.0.0.1" or "localhost" or "::1");
        return true;
    }

    public override string ToString() => Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Where the desk gets a link to a real engine. The TCP link (the Net module's EngineLinkClient) is
/// the integrator's to supply: implement <see cref="CreateNetworkLink"/> in another part of this class (a
/// file in the composition that references Net) and the desk's Connect choices for "This machine" and "A
/// node on the network" use it. Without it, the desk offers the demo engine only and says why.</summary>
#pragma warning disable S3251 // The integrator implements these partial methods in its composition file; a desk-only build runs the demo engine.
public static partial class EngineLinks
{
    /// <summary>A link to the engine at the endpoint, or null when this build has none.</summary>
    public static IEngineLink? Create(EngineEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        IEngineLink? link = null;
        CreateNetworkLink(endpoint, ref link);
        return link;
    }

    /// <summary>True when the build supplies a real link.</summary>
    public static bool Available
    {
        get
        {
            var available = false;
            HasNetworkLink(ref available);
            return available;
        }
    }

    /// <summary>This machine's node pairing token, read from its settings where they live (never from a
    /// line); null when this build cannot read it.</summary>
    public static string? ReadLocalToken()
    {
        string? token = null;
        ReadLocalPairingToken(ref token);
        return token;
    }

    static partial void CreateNetworkLink(EngineEndpoint endpoint, ref IEngineLink? link);

    static partial void HasNetworkLink(ref bool available);

    static partial void ReadLocalPairingToken(ref string? token);
}
#pragma warning restore S3251

/// <summary>"PlayMap engine [headless] …": the node's entry point. The engine module owns it; the desk
/// only routes to it. Implement <see cref="RunEngine"/> in another part of this class (the integrator's
/// composition file) to start the node; without it this build is a desk only and says so.</summary>
#pragma warning disable S3251 // The engine module's composition implements RunEngine; without it this build is a desk only and says so.
public static partial class EngineEntry
{
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var exitCode = int.MinValue;
        RunEngine(args, ref exitCode);
        if (exitCode != int.MinValue)
            return exitCode;
        System.Diagnostics.Trace.WriteLine("PlayMap: this build has no engine; run the desk, or a build with the engine module.");
        return 2;
    }

    static partial void RunEngine(string[] args, ref int exitCode);
}
#pragma warning restore S3251
