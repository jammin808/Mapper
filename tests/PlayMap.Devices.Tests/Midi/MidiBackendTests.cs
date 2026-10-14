using PlayMap.Core.Control.Midi;
using PlayMap.Devices.Midi;

namespace PlayMap.Devices.Tests.Midi;

/// <summary>The machine's MIDI backend: WinMM on Windows (compile-only here; the Windows CI job runs it and finds no ports on a runner
/// without MIDI), the ALSA sequencer on Linux (it runs here and says why it has no ports), the fake for tests; and port-name globs.</summary>
public sealed class MidiBackendTests
{
    private sealed class Collect : IMidiReceiver
    {
        public List<byte[]> Messages { get; } = [];

        public void Receive(ReadOnlySpan<byte> message, long monoNs) => Messages.Add(message.ToArray());
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ThisMachinesBackendListsItsPortsOrSaysWhyItHasNone()
    {
        using var backend = MidiBackends.ForThisMachine("");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("winmm", backend.Name);
            Assert.Null(backend.Unavailable);
            // A port listed is an input with a name; a runner without MIDI lists none, and the node's STATE says so in words.
            Assert.All(backend.Ports(), p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("alsa-seq", backend.Name);
            if (backend.Unavailable is { } why)
            {
                Assert.Empty(backend.Ports());
                Assert.StartsWith("no ALSA", why, StringComparison.Ordinal);
            }
            return;
        }
        Assert.Equal("none", backend.Name);
        Assert.NotNull(backend.Unavailable);
    }

    [Fact]
    public void TheFakeBackendCarriesBytesBothWaysAndForgetsAClosedInput()
    {
        using var fake = new FakeMidiBackend();
        var changed = 0;
        fake.PortsChanged += () => changed++;
        fake.Plug("APC40 mkII");
        Assert.Equal(1, changed);
        var input = fake.Ports().Single(p => p.IsInput);
        var heard = new Collect();
        var open = fake.OpenInput(input, heard);
        Assert.Equal(1, fake.Play("APC40 mkII", [0x90, 0x35, 0x7F], 1));
        Assert.Equal([0x90, 0x35, 0x7F], heard.Messages.Single());
        open.Dispose();
        Assert.Equal(0, fake.Play("APC40 mkII", [0x80, 0x35, 0x00], 2));
        using var output = fake.OpenOutput(fake.Ports().Single(p => !p.IsInput));
        Assert.True(output.Send([0xB0, 0x07, 0x40]));
        Assert.Equal([0xB0, 0x07, 0x40], fake.Sent("APC40 mkII").Single());
        fake.Unplug("APC40 mkII");
        Assert.Throws<MidiUnavailableException>(() => fake.OpenInput(input, heard));
    }

    [Theory]
    [InlineData("APC40*", "APC40 mkII", true)]
    [InlineData("apc40*", "APC40 mkII", true)]
    [InlineData("*mkII", "APC40 mkII", true)]
    [InlineData("APC4?", "APC40", true)]
    [InlineData("APC4?", "APC400", false)]
    [InlineData("Launchpad*", "APC40 mkII", false)]
    public void APortGlobMatchesNamesWithoutCase(string glob, string name, bool match) => Assert.Equal(match, PortGlob.Matches(glob, name));
}
