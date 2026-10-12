using System.Collections.Concurrent;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Tests;

/// <summary>Links, credentials and a collecting log for the driver tests.</summary>
internal static class TestSupport
{
    public static ProjectorLink Link(ProjectorProtocol protocol, int port, string credentialRef = "", bool allowUnverified = false, int pollMs = 0) =>
        new("dv00000001", "Stage Left", protocol, "127.0.0.1", port, credentialRef, "pj00000001", "op00000001", pollMs, allowUnverified);

    public static InMemoryCredentialStore Store(string reference, string user, string secret)
    {
        var store = new InMemoryCredentialStore();
        store.Set(reference, new Credential(user, secret));
        return store;
    }

    public static InMemoryCredentialStore NoSecrets() => new();
}

/// <summary>A log that keeps every line, for asserts on what was written.</summary>
internal sealed class CollectingLog : IDeviceLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Write(string line) => Lines.Enqueue(line);

    public string All => string.Join("\n", Lines);
}
