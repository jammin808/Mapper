using PlayMap.Engine.Optimise;
using PlayMap.Engine.Show;
using PlayMap.Net.Control;

namespace PlayMap.Engine.Tests.Hot;

/// <summary>The methods a show-lane tick, a control poll, a service step or a frame's send calls make no closure: each lambda they need lives in a
/// method of its own that runs only when it is needed (a decoder opens, a worker starts or stops, a socket opens). Read from the IL,
/// so the rule holds in the Debug build too, where the compiler would make the closure only inside its branch; the allocation an
/// optimised build would make at each call's entry is measured in <see cref="Tests.Show.AirAudioTests"/>.</summary>
public sealed class HotPathClosureTests
{
    public static TheoryData<string> HotMethods() =>
    [
        "AirAudio.Follow", "OptimiseSupervisor.EnsureStarted", "OptimiseSupervisor.StopAsync", "ArtNetEndpoint.Open",
        "EngineCore.TickBlind", "EngineCore.TickNative", "EngineCore.TickPanic", "EngineCore.TickTimecode", "OscFeedback.Pass",
    ];

    private static System.Reflection.MethodInfo Find(string name) => name switch
    {
        "AirAudio.Follow" => ClosureAudit.Method(typeof(AirAudio), "Follow", typeof(EngineSnapshot), typeof(long)),
        "OptimiseSupervisor.EnsureStarted" => ClosureAudit.Method(typeof(OptimiseSupervisor), "EnsureStarted"),
        "OptimiseSupervisor.StopAsync" => ClosureAudit.Method(typeof(OptimiseSupervisor), "StopAsync", Type.EmptyTypes),
        "ArtNetEndpoint.Open" => ClosureAudit.Method(typeof(ArtNetEndpoint), "Open"),
        "OscFeedback.Pass" => ClosureAudit.Method(typeof(Engine.Control.OscFeedback), "Pass"),
        _ when name.StartsWith("EngineCore.", StringComparison.Ordinal) => ClosureAudit.Method(typeof(EngineCore), name["EngineCore.".Length..]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(HotMethods))]
    public void AHotMethodMakesNoClosure(string method) =>
        Assert.Empty(ClosureAudit.DisplayClassesMade(Find(method)));

    [Fact]
    public void TheAuditSeesAClosureWhereOneIsMade()
    {
        // The helpers hold the lambdas the hot methods let go of: the audit finds each one's closure.
        Assert.NotEmpty(ClosureAudit.DisplayClassesMade(ClosureAudit.Method(typeof(AirAudio), "StartOpen", typeof(string))));
        Assert.NotEmpty(ClosureAudit.DisplayClassesMade(ClosureAudit.Method(typeof(ArtNetEndpoint), "StartReceiving", typeof(System.Net.Sockets.Socket))));
    }
}
