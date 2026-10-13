using PlayMap.Core.Wire;
using PlayMap.Engine;
using PlayMap.Net.Links;

namespace PlayMap.App.Link;

/// <summary>The desk's links to a real engine: the Net module's engine link (the line wire, pairing by proof, STATE
/// and the project mirrored latest-wins), started as it is made so it dials — and redials — until the engine answers.
/// To this machine's own engine the link proves the node's own pairing token when loopback is not trusted.</summary>
public static partial class EngineLinks
{
    static partial void CreateNetworkLink(EngineEndpoint endpoint, ref IEngineLink? link)
    {
        var token = endpoint.Token ?? (endpoint.Local ? ReadLocalToken() : null) ?? "";
        var client = new EngineLinkClient(endpoint.Host, endpoint.Port, token);
        client.Start();
        link = client;
    }

    static partial void HasNetworkLink(ref bool available) => available = true;

    /// <summary>The token of this machine's node, read from its settings file in the node's own folder (the one the
    /// desk's engine runs with). Nothing is made when the node has never run, and the token is never logged.</summary>
    static partial void ReadLocalPairingToken(ref string? token)
    {
        try
        {
            var home = NodeHome.Default();
            if (File.Exists(home.SettingsPath))
                token = home.LoadSettings(Environment.MachineName).Token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            token = null;   // unreadable now: the desk asks again when it next needs it
        }
    }
}

/// <summary>"PlayMap engine [headless] …" runs the node: the engine module's entry, with the exe's standard parts.</summary>
public static partial class EngineEntry
{
    static partial void RunEngine(string[] args, ref int exitCode) => exitCode = Engine.Host.EngineEntry.Run(args);
}
