using PlayMap.Core.Machine;

namespace PlayMap.Core.Tests.Machine;

/// <summary>The engine's stop event names: each drawn under the prefix with 32 lower-case hex digits no account can foresee, so none is
/// made before the supervisor makes it; only a name of that shape is ever taken from a command line, so no reader opens, and no elevated
/// stop sets, an event a command line chose; and only SYSTEM or administrators are owners an event is used from.</summary>
public sealed class SupervisorNamesTests
{
    [Fact]
    public void ANewStopEventIsThePrefixAndThirtyTwoDrawnHexDigits()
    {
        var names = Enumerable.Range(0, 64).Select(_ => SupervisorNames.NewStopEvent(SupervisorNames.StopEventPrefix)).ToList();
        Assert.All(names, n =>
        {
            Assert.StartsWith(@"Global\PlayMap-Engine-Stop-", n, StringComparison.Ordinal);
            Assert.Equal(SupervisorNames.StopEventPrefix.Length + 32, n.Length);
            Assert.True(SupervisorNames.IsStopEvent(n, SupervisorNames.StopEventPrefix), n);
        });
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.True(SupervisorNames.IsStopEvent(SupervisorNames.NewStopEvent(@"Local\Test-"), @"Local\Test-"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Global\PlayMap-Engine-Stop")]
    [InlineData(@"Global\PlayMap-Engine-Stop-")]
    [InlineData(@"Global\PlayMap-Engine-Stop-0123456789abcdef0123456789abcde")]
    [InlineData(@"Global\PlayMap-Engine-Stop-0123456789abcdef0123456789abcdef0")]
    [InlineData(@"Global\PlayMap-Engine-Stop-0123456789ABCDEF0123456789abcdef")]
    [InlineData(@"Global\PlayMap-Engine-Stop-0123456789abcdef0123456789abcdeg")]
    [InlineData(@"Local\PlayMap-Engine-Stop-0123456789abcdef0123456789abcdef")]
    [InlineData(@"Global\SomeService-Shutdown-0123456789abcdef0123456789abcdef")]
    public void ANameOfAnyOtherShapeIsNoStopEvent(string? name) =>
        Assert.False(SupervisorNames.IsStopEvent(name, SupervisorNames.StopEventPrefix));

    [Fact]
    public void TheStopEventIsReadFromTheEnginesCommandLineOnlyWhenItIsOne()
    {
        var name = SupervisorNames.NewStopEvent(SupervisorNames.StopEventPrefix);
        var exe = "\"C:\\Program Files\\PlayMap\\PlayMap.exe\"";
        Assert.Equal(name, SupervisorNames.StopEventIn($"{exe} engine --supervised --stop-event {name}", SupervisorNames.StopEventPrefix));
        Assert.Equal(name, SupervisorNames.StopEventIn($"{exe} engine --stop-event {name} --supervised", SupervisorNames.StopEventPrefix));
        // The engine takes the last one it is given; so does every reader.
        var other = SupervisorNames.NewStopEvent(SupervisorNames.StopEventPrefix);
        Assert.Equal(other, SupervisorNames.StopEventIn($"{exe} engine --supervised --stop-event {name} --stop-event {other}", SupervisorNames.StopEventPrefix));
        Assert.Null(SupervisorNames.StopEventIn($"{exe} engine --supervised", SupervisorNames.StopEventPrefix));
        Assert.Null(SupervisorNames.StopEventIn($"{exe} engine --supervised --stop-event", SupervisorNames.StopEventPrefix));
        Assert.Null(SupervisorNames.StopEventIn($"{exe} engine --supervised --stop-event Global\\SomeService-Shutdown", SupervisorNames.StopEventPrefix));
        Assert.Null(SupervisorNames.StopEventIn($"{exe} engine --supervised --stop-event \"{name}\"", SupervisorNames.StopEventPrefix));
        Assert.Null(SupervisorNames.StopEventIn($"{exe} engine --supervised --stop-event {name}", @"Local\Test-"));
        Assert.Null(SupervisorNames.StopEventIn("", SupervisorNames.StopEventPrefix));
        Assert.Null(SupervisorNames.StopEventIn(null, SupervisorNames.StopEventPrefix));
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-21-1000-2000-3000-1001", false)]
    [InlineData("S-1-5-4", false)]
    [InlineData("S-1-1-0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AStopEventIsUsedOnlyFromSystemOrAdministrators(string? owner, bool trusted) =>
        Assert.Equal(trusted, SupervisorNames.StopEventOwnerTrusted(owner));
}
