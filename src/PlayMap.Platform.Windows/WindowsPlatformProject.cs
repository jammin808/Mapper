using System.Runtime.Versioning;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;

namespace PlayMap.Platform.Windows;

/// <summary>The Windows machine platform as a node composes it: <see cref="WindowsMachinePlatform"/>, which claims what a show node
/// needs from Windows and says what Windows answered.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPlatformProject
{
    /// <summary>The node's platform on its clocks, with the runtimes their owners fill.</summary>
    public static WindowsMachinePlatform Create(IMonoClock clock, Func<long> unixMs, Func<RuntimeIdentity>? runtimes = null) =>
        new(clock, unixMs, runtimes);
}
