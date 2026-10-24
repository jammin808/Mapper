using System.Collections.Concurrent;

namespace PlayMap.Engine.Tests;

/// <summary>The operations services an engine built without the host owns: let go in reverse order of attachment, once, and a
/// part whose dispose throws keeps none of the parts before it.</summary>
public sealed class OpsServicesTests
{
    [Fact]
    public async Task APartWhoseDisposeThrowsLetsThePartsBeforeItGoAndIsThrownOnceTheyAre()
    {
        var gone = new ConcurrentQueue<string>();
        var owned = new OpsServices.Owned([
            new Part("scan runner", gone),
            new Part("devices", gone),
            new Part("devices' attachment", gone),
            new Part("assistant", gone, "the assistant's desk would not go"),
            new Part("assistant's attachment", gone),
        ]);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await owned.DisposeAsync());
        Assert.Equal("the assistant's desk would not go", thrown.Message);
        Assert.Equal(["assistant's attachment", "assistant", "devices' attachment", "devices", "scan runner"], gone);

        // Once: a second dispose lets nothing go again and throws nothing.
        await owned.DisposeAsync();
        Assert.Equal(5, gone.Count);
    }

    [Fact]
    public async Task EveryFailureIsThrownOnceEveryPartIsLetGo()
    {
        var gone = new ConcurrentQueue<string>();
        var owned = new OpsServices.Owned([
            new Part("scan runner", gone, "the scan runner would not go"),
            new Part("devices", gone),
            new Part("assistant", gone, "the assistant would not go"),
        ]);

        var thrown = await Assert.ThrowsAsync<AggregateException>(async () => await owned.DisposeAsync());
        Assert.Equal(["the assistant would not go", "the scan runner would not go"], thrown.InnerExceptions.Select(e => e.Message));
        Assert.Equal(["assistant", "devices", "scan runner"], gone);
    }

    /// <summary>A part that says when it goes, and throws when it is given words to throw.</summary>
    private sealed class Part(string name, ConcurrentQueue<string> gone, string? fails = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gone.Enqueue(name);
            return fails is null ? ValueTask.CompletedTask : ValueTask.FromException(new InvalidOperationException(fails));
        }
    }
}
