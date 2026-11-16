namespace PlayMap.HangProbe;

/// <summary>A test host that hangs, on purpose: the one test waits on an event nobody sets, burning no processor, so only the blame
/// collector (or the step's own deadline around it) ends the host. The stacks a hang dump reads name this method.</summary>
public sealed class NeverEndsTests
{
    private static readonly ManualResetEventSlim Nobody = new(false);

    [Fact]
    public void WaitsOnAnEventNobodySets()
    {
        Nobody.Wait();
        Assert.Fail("an event nobody sets was set");
    }
}
