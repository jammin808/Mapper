using System.Collections.Immutable;
using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>A listener fed by a device's capture thread claims its role on that thread (pure, both jobs): two listeners on one fake
/// device claim on its one capture thread at their first delivery and never again while it delivers; a device reopened on a new thread
/// gives each listener's old claim back before claiming on the new one; disposing a listener gives back the claim it holds, and a
/// delivery after that claims nothing.</summary>
public sealed class DeliveryClaimTests
{
    /// <summary>Where the listeners claim: each claim's role and thread, and which claims were given back.</summary>
    private sealed class Claims
    {
        private readonly Lock _gate = new();
        private int _next;

        public List<(int Id, ThreadRole Role, int Thread)> Made { get; } = [];

        public List<int> Given { get; } = [];

        public IThreadClaim Claim(ThreadRole role)
        {
            lock (_gate)
            {
                var id = ++_next;
                Made.Add((id, role, Environment.CurrentManagedThreadId));
                return new Held(this, id);
            }
        }

        private void GiveBack(int id)
        {
            lock (_gate)
                Given.Add(id);
        }

        private sealed class Held(Claims owner, int id) : IThreadClaim
        {
            public ImmutableArray<Claim> Claims => [];

            public void Dispose() => owner.GiveBack(id);
        }
    }

    /// <summary>A device whose capture thread hands each block to every listener, as the audio hub fans one device out.</summary>
    private static int Deliver(IReadOnlyList<DeliveryClaim> listeners, int blocks)
    {
        var thread = 0;
        var capture = new Thread(() =>
        {
            thread = Environment.CurrentManagedThreadId;
            for (var b = 0; b < blocks; b++)
                foreach (var l in listeners)
                    l.Here();
        });
        capture.Start();
        capture.Join();
        return thread;
    }

    [Fact]
    public void TwoListenersOnOneDeviceClaimOnItsCaptureThreadAndClaimAgainWhenItsThreadChanges()
    {
        var claims = new Claims();
        using var a = new DeliveryClaim(ThreadRole.Timecode, claims.Claim);
        using var b = new DeliveryClaim(ThreadRole.Timecode, claims.Claim);
        Assert.Equal((0, 0), (a.Thread, b.Thread));

        var first = Deliver([a, b], blocks: 50);
        // One claim each, on the capture thread, however many blocks it delivered.
        Assert.Equal([(1, ThreadRole.Timecode, first), (2, ThreadRole.Timecode, first)], claims.Made);
        Assert.Equal((first, first), (a.Thread, b.Thread));
        Assert.Empty(claims.Given);

        // The device reopens on a new capture thread (which may take the old one's id): each listener gives its old claim back, then
        // claims on the new thread.
        var second = Deliver([a, b], blocks: 20);
        Assert.Equal([1, 2], claims.Given);
        Assert.Equal([(3, ThreadRole.Timecode, second), (4, ThreadRole.Timecode, second)], claims.Made.Skip(2));
        Assert.Equal((second, second), (a.Thread, b.Thread));

        // A listener disposed gives back what it holds; the other keeps its claim, and a late delivery to the disposed one claims nothing.
        a.Dispose();
        Assert.Equal([1, 2, 3], claims.Given);
        Assert.Null(a.Held);
        Assert.NotNull(b.Held);
        _ = Deliver([a], blocks: 3);
        Assert.Equal(4, claims.Made.Count);
        Assert.Equal(0, a.Thread);
    }

    [Fact]
    public void ADeliveryOnTheClaimedThreadClaimsNothingMore()
    {
        var claims = new Claims();
        using var listener = new DeliveryClaim(ThreadRole.Audio, claims.Claim);
        for (var i = 0; i < 1000; i++)
            listener.Here();
        var made = Assert.Single(claims.Made);
        Assert.Equal((ThreadRole.Audio, Environment.CurrentManagedThreadId), (made.Role, made.Thread));
        listener.Dispose();
        listener.Dispose();
        Assert.Equal([1], claims.Given);
    }
}
