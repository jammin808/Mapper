using PlayMap.Render.Platform.Windows;

namespace PlayMap.Render.Sync;

/// <summary>The framelock tier a node starts with: NVAPI on Windows, nothing elsewhere (with the words why).</summary>
public static class FrameLockDrivers
{
    public static IFrameLockDriver ForThisMachine() =>
        OperatingSystem.IsWindows() ? NvFrameLock.Create() : new NoFrameLock("NVAPI is Windows only: outputs present free, measured");
}
