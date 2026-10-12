namespace PlayMap.App.Tests;

/// <summary>The running test's cancellation token, so a cancelled run stops what a test sent.</summary>
internal static class TestCt
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
