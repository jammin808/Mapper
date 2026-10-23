using System.Collections.Concurrent;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Model;
using PlayMap.Tests;

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

    /// <summary>Lets a search whose window is counted on <paramref name="time"/> end once it has what it waits for: the first answer
    /// heard (<paramref name="heard"/>; its hit is collected before the socket says it heard it), and the window's wait standing on the
    /// clock; then the clock is moved through <paramref name="window"/>. However late the answer comes, it is in the hits.</summary>
    public static async Task EndSearchAsync(ManualTime time, TimeSpan window, Task heard)
    {
        var patience = TimeSpan.FromSeconds(10);
        await heard.WaitAsync(patience);
        Assert.True(SpinWait.SpinUntil(() => time.Waiting >= 1, patience), "the search's window never stood on its clock");
        time.Advance(window);
    }

    /// <summary>Completes when <paramref name="udp"/> hears a search's answer.</summary>
    public static Task Answered(PjLinkUdp udp)
    {
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        udp.Noticed += (_, e) =>
        {
            if (e.Notice.Kind == PjLinkNoticeKind.SearchAck)
                heard.TrySetResult();
        };
        return heard.Task;
    }
}

/// <summary>A log that keeps every line, for asserts on what was written.</summary>
internal sealed class CollectingLog : IDeviceLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Write(string line) => Lines.Enqueue(line);

    public string All => string.Join("\n", Lines);
}
