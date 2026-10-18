using System.Reflection;
using System.Runtime.InteropServices;
using libomtnet;

namespace PlayMap.Media.Video.Omt;

/// <summary>How this node runs OMT: where its settings and logs would live (the node's own folder, never the user's OMT folder),
/// the discovery server and the sender port range, all set in memory before OMT's first use and never saved.</summary>
public sealed record OmtOptions(string StoragePath, string DiscoveryServer = "", int PortStart = 6400, int PortEnd = 6600);

/// <summary>OMT's process-wide setup, done once before anything of OMT runs: its storage folder (OMT_STORAGE_PATH) set to the node's,
/// file logging off, the discovery server and port range set in OMT's settings in memory (P5 in src/ThirdParty/libomtnet/PATCHES.md),
/// and the resolver that hands OMT its native codec, libvmx, loaded at run time from <c>PLAYMAP_VMX_DIR</c>, then beside the exe, then
/// the exe's <c>runtimes/&lt;rid&gt;/native</c>, then the system's library folders (Linux, macOS). Every candidate is an absolute
/// path, loaded by that full path: the system's search by bare name, which on Windows takes in the current directory and PATH, is
/// never asked — nor is the runtime's own probing when libvmx is missing, which the resolver refuses with the words — and a relative
/// <c>PLAYMAP_VMX_DIR</c> is refused with the words (STATE's <c>videoio.omt.words</c>), never read against the current directory.
/// OMT's settings are per process: the first configuration wins.</summary>
public static class OmtSetup
{
    public const string VmxDirVariable = "PLAYMAP_VMX_DIR";

    private static readonly Lock Gate = new();
    private static bool s_configured;
    private static bool s_resolver;
    private static nint s_vmx;
    private static string s_vmxPath = "";
    private static string s_vmxWhy = "";
    private static string s_vmxRefused = "";

    /// <summary>The file name libvmx has on this platform.</summary>
    public static string VmxFileName => OperatingSystem.IsWindows() ? "libvmx.dll" : OperatingSystem.IsMacOS() ? "libvmx.dylib" : "libvmx.so";

    /// <summary>Configures OMT for this process (once; later calls keep the first configuration) and answers whether libvmx loaded.</summary>
    public static bool Configure(OmtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (Gate)
        {
            InstallResolver();
            // A codec put in place after an earlier look (a test that builds it, a user who copies it) is found on the next one.
            if (s_vmx == 0)
                Look();
            if (!s_configured)
            {
                s_configured = true;
                Directory.CreateDirectory(options.StoragePath);
                Environment.SetEnvironmentVariable("OMT_STORAGE_PATH", options.StoragePath);
                // A file name of nothing marks OMT's log as set up with no file: nothing is written under the node or the user.
                OMTLogging.SetFilename(null);
                var settings = OMTSettings.GetInstance();
                if (options.DiscoveryServer.Length > 0)
                    settings.SetString("DiscoveryServer", ServerUrl(options.DiscoveryServer));
                settings.SetInteger("NetworkPortStart", options.PortStart);
                settings.SetInteger("NetworkPortEnd", options.PortEnd);
            }
            return s_vmx != 0;
        }
    }

    /// <summary>"host[:port]" as OMT's discovery server URL (port 6399 when none is given).</summary>
    public static string ServerUrl(string server)
    {
        var s = server.Trim();
        if (s.StartsWith("omt://", StringComparison.OrdinalIgnoreCase))
            return s;
        return s.Contains(':', StringComparison.Ordinal) ? "omt://" + s : "omt://" + s + ":6399";
    }

    /// <summary>Where libvmx was loaded from ("" when it was not).</summary>
    public static string VmxPath
    {
        get
        {
            lock (Gate)
                return s_vmxPath;
        }
    }

    /// <summary>Why libvmx did not load, in words ("" when it did).</summary>
    public static string VmxWhy
    {
        get
        {
            lock (Gate)
                return s_vmxWhy;
        }
    }

    /// <summary>The value refused while looking for libvmx (a <c>PLAYMAP_VMX_DIR</c> that is not an absolute path), in words ("" when
    /// none was).</summary>
    public static string VmxRefused
    {
        get
        {
            lock (Gate)
                return s_vmxRefused;
        }
    }

    /// <summary>The codec's CPU path on this machine: avx2, sse42, neon, or missing when libvmx did not load.</summary>
    public static string VmxPathWord
    {
        get
        {
            if (VmxPath.Length == 0)
                return "missing";
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                return "avx2";
            if (System.Runtime.Intrinsics.X86.Sse42.IsSupported)
                return "sse42";
            return System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported ? "neon" : "scalar";
        }
    }

    /// <summary>Service discovery this machine offers OMT: avahi when its daemon runs (Linux), dnsapi (Windows), bonjour (macOS),
    /// none otherwise.</summary>
    public static string Mdns()
    {
        if (OperatingSystem.IsWindows())
            return "dnsapi";
        if (OperatingSystem.IsMacOS())
            return "bonjour";
        return File.Exists("/run/avahi-daemon/socket") || File.Exists("/var/run/avahi-daemon/socket") ? "avahi" : "none";
    }

    /// <summary>Where libvmx is looked for, in order: <c>PLAYMAP_VMX_DIR</c>, beside the exe, the exe's <c>runtimes/&lt;rid&gt;/native</c>,
    /// then the system's library folders — absolute files only — and the words for a variable refused because it was not absolute.</summary>
    public static NativeSearch VmxSearch()
    {
        var files = new List<string>();
        var refused = new List<string>();
        void Add(string? dir)
        {
            // Only an absolute folder is looked in: a relative one would be read against the current directory.
            if (dir is null || !Path.IsPathFullyQualified(dir))
                return;
            var file = Path.Combine(dir, VmxFileName);
            if (!files.Contains(file, StringComparer.Ordinal))
                files.Add(file);
        }
        Add(NativePaths.Absolute(Environment.GetEnvironmentVariable(VmxDirVariable), VmxDirVariable, refused));
        var exe = AppContext.BaseDirectory;
        Add(exe);
        Add(Path.Combine(exe, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        foreach (var dir in NativePaths.SystemFolders())
            Add(dir);
        return new NativeSearch(files, refused);
    }

    /// <summary>Loads libvmx from the first of the search's files that exists and loads, by that full path; zero, with the words that
    /// say what was searched and what was refused, when none does.</summary>
    internal static nint LoadVmx(NativeSearch search, out string loadedFrom, out string why)
    {
        ArgumentNullException.ThrowIfNull(search);
        var refused = new List<string>(search.Refused);
        foreach (var file in search.Files)
        {
            if (!File.Exists(file))
                continue;
            if (NativeLibrary.TryLoad(file, out var handle))
            {
                loadedFrom = file;
                why = "";
                return handle;
            }
            refused.Add($"{file} would not load");
        }
        loadedFrom = "";
        why = $"libvmx (OMT's video codec) was not found: searched {string.Join(", ", search.Files)}"
            + (refused.Count > 0 ? " (" + string.Join("; ", refused) + ")" : "")
            + $" — build it from src/ThirdParty/libvmx (build/) and put it beside the exe or set {VmxDirVariable} to its folder, an absolute path";
        return 0;
    }

    /// <summary>The resolver's answer for a library OMT imports: libvmx's handle, or the refusal when it is not loaded — zero would hand
    /// the name to the runtime's own probing, whose search by name takes in the current directory and PATH. Any other library is
    /// left to the runtime (zero).</summary>
    internal static nint Resolved(string name, nint vmx, string why)
    {
        if (!string.Equals(name, "libvmx", StringComparison.Ordinal))
            return 0;
        return vmx != 0 ? vmx : throw new DllNotFoundException(why);
    }

    private static void InstallResolver()
    {
        if (s_resolver)
            return;
        s_resolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OMTSend).Assembly, Resolve);
        Look();
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        lock (Gate)
            return Resolved(name, s_vmx, s_vmxWhy);
    }

    private static void Look()
    {
        var search = VmxSearch();
        s_vmxRefused = search.RefusedWords;
        s_vmx = LoadVmx(search, out s_vmxPath, out s_vmxWhy);
    }
}
