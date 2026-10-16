using System.Runtime.InteropServices;

namespace PlayMap.Media.Video.Ndi;

/// <summary>Sets a variable where native code reads it: on Unix .NET keeps its own copy of the environment, so a native library's
/// getenv (the NDI runtime's NDI_CONFIG_DIR) sees only what libc's setenv put there. Call before the library reads it, from one
/// thread (setenv is not safe beside another thread's getenv).</summary>
internal static partial class NativeEnvironment
{
    public static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows() && SetEnv(name, value, 1) != 0)
            throw new IOException($"setenv({name}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
