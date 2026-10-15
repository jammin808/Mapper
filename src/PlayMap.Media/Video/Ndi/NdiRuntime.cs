using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PlayMap.Media.Video.Ndi;

/// <summary>The NDI® runtime the user installed, loaded at run time — none ships with PlayMap. The first of these that loads wins:
/// the node's setting (<c>videoio.ndiRuntimePath</c>, a file or a folder), <c>PLAYMAP_NDI_DIR</c>, the runtime's own variable
/// <c>NDI_RUNTIME_DIR_V6</c>, the exe's folder (only because the user put it there), the standard install folders, then the
/// system's loader by name. Its exports are bound once as unmanaged function pointers (no marshalling stubs): the ones every node
/// needs are required, the 6.x extras (source names, advertisers) optional and said in STATE. A runtime is loaded once per process
/// per file and never unloaded.</summary>
public sealed unsafe partial class NdiRuntime
{
    public const string DirVariable = "PLAYMAP_NDI_DIR";
    public const string RuntimeVariable = "NDI_RUNTIME_DIR_V6";
    public const string Link = "https://ndi.video";
    public const string RedistLink = "https://ndi.link/NDIRedistV6";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, NdiRuntime> Loaded = new(StringComparer.Ordinal);

    internal delegate* unmanaged[Cdecl]<byte> Initialize;
    internal delegate* unmanaged[Cdecl]<void> Destroy;
    internal delegate* unmanaged[Cdecl]<nint> VersionString;
    internal delegate* unmanaged[Cdecl]<byte> IsSupportedCpu;
    internal delegate* unmanaged[Cdecl]<NdiFindCreate*, nint> FindCreate;
    internal delegate* unmanaged[Cdecl]<nint, void> FindDestroy;
    internal delegate* unmanaged[Cdecl]<nint, uint, byte> FindWaitForSources;
    internal delegate* unmanaged[Cdecl]<nint, uint*, NdiSourceT*> FindGetCurrentSources;
    internal delegate* unmanaged[Cdecl]<NdiRecvCreate*, nint> RecvCreate;
    internal delegate* unmanaged[Cdecl]<nint, void> RecvDestroy;
    internal delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void*, NdiMetadataFrame*, uint, int> RecvCapture;
    internal delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void> RecvFreeVideo;
    internal delegate* unmanaged[Cdecl]<nint, NdiMetadataFrame*, void> RecvFreeMetadata;
    internal delegate* unmanaged[Cdecl]<nint, NdiTally*, byte> RecvSetTally;
    internal delegate* unmanaged[Cdecl]<nint, int> RecvGetNoConnections;
    internal delegate* unmanaged[Cdecl]<NdiSendCreate*, nint> SendCreate;
    internal delegate* unmanaged[Cdecl]<nint, void> SendDestroy;
    internal delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void> SendVideoAsync;
    internal delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void> SendVideo;
    internal delegate* unmanaged[Cdecl]<nint, NdiTally*, uint, byte> SendGetTally;
    internal delegate* unmanaged[Cdecl]<nint, uint, int> SendGetNoConnections;
    internal delegate* unmanaged[Cdecl]<nint, NdiSourceT*> SendGetSourceName;
    internal delegate* unmanaged[Cdecl]<NdiAdvertiserCreate*, nint> SendAdvertiserCreate;
    internal delegate* unmanaged[Cdecl]<nint, void> SendAdvertiserDestroy;
    internal delegate* unmanaged[Cdecl]<nint, nint, byte, byte> SendAdvertiserAddSender;
    internal delegate* unmanaged[Cdecl]<nint, nint, byte> SendAdvertiserDelSender;
    internal delegate* unmanaged[Cdecl]<NdiAdvertiserCreate*, nint> RecvAdvertiserCreate;
    internal delegate* unmanaged[Cdecl]<nint, void> RecvAdvertiserDestroy;
    internal delegate* unmanaged[Cdecl]<nint, nint, byte, byte, nint, byte> RecvAdvertiserAddReceiver;
    internal delegate* unmanaged[Cdecl]<nint, nint, byte> RecvAdvertiserDelReceiver;

    private NdiRuntime(string path, nint handle)
    {
        Path = path;
        Handle = handle;
    }

    /// <summary>The file it was loaded from (or the loader name it was found by).</summary>
    public string Path { get; }

    internal nint Handle { get; }

    /// <summary>NDIlib_version's words ("NDI SDK LINUX 6.3.2 …").</summary>
    public string Version { get; private set; } = "";

    /// <summary>The version's numbers (0.0.0 when it names none).</summary>
    public Version Numbers { get; private set; } = new(0, 0, 0);

    /// <summary>6.3's send and receive advertisers are exported (and the runtime says 6.3 or later).</summary>
    public bool HasAdvertisers => SendAdvertiserCreate != null && RecvAdvertiserCreate != null && Numbers >= new Version(6, 3);

    /// <summary>The library file's name on this platform.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? "Processing.NDI.Lib.x64.dll" : OperatingSystem.IsMacOS() ? "libndi.dylib" : "libndi.so.6";

    /// <summary>Where the runtime is looked for, in order (files; the last is the loader's own search by name).</summary>
    public static IReadOnlyList<string> Candidates(string configured)
    {
        var list = new List<string>();
        void Add(string? place)
        {
            if (string.IsNullOrWhiteSpace(place))
                return;
            var file = Directory.Exists(place) || !System.IO.Path.HasExtension(place) && !place.EndsWith(".6", StringComparison.Ordinal)
                ? System.IO.Path.Combine(place, FileName)
                : place;
            if (!list.Contains(file, StringComparer.Ordinal))
                list.Add(file);
        }
        Add(configured);
        Add(Environment.GetEnvironmentVariable(DirVariable));
        Add(Environment.GetEnvironmentVariable(RuntimeVariable));
        Add(AppContext.BaseDirectory);
        if (OperatingSystem.IsWindows())
        {
            Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NDI", "NDI 6 Runtime", "v6"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Add("/usr/local/lib");
            Add("/Library/NDI SDK for Apple/lib/macOS");
        }
        else
        {
            Add("/usr/lib");
            Add("/usr/local/lib");
            Add("/usr/lib/x86_64-linux-gnu");
        }
        list.Add(FileName);
        return list;
    }

    /// <summary>Loads (once per file) the first runtime of <see cref="Candidates"/> that loads and has every required export, or
    /// null with the words that say what was searched, why each was refused and where to get the runtime.</summary>
    public static NdiRuntime? Load(string configured, out string why)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var refused = new List<string>();
        foreach (var candidate in Candidates(configured))
        {
            lock (Gate)
            {
                if (Loaded.TryGetValue(candidate, out var known))
                {
                    why = "";
                    return known;
                }
            }
            var byName = !System.IO.Path.IsPathRooted(candidate);
            if (!byName && !File.Exists(candidate))
                continue;
            if (!NativeLibrary.TryLoad(candidate, out var handle))
            {
                if (!byName)
                    refused.Add($"{candidate} would not load");
                continue;
            }
            var runtime = new NdiRuntime(candidate, handle);
            if (runtime.Bind() is { } missing)
            {
                refused.Add($"{candidate} lacks {missing}");
                continue;
            }
            runtime.Version = Marshal.PtrToStringUTF8(runtime.VersionString()) ?? "";
            runtime.Numbers = ParseVersion(runtime.Version);
            lock (Gate)
            {
                if (Loaded.TryGetValue(candidate, out var raced))
                {
                    why = "";
                    return raced;
                }
                Loaded[candidate] = runtime;
            }
            why = "";
            return runtime;
        }
        var searched = string.Join(", ", Candidates(configured).Where(System.IO.Path.IsPathRooted));
        why = $"the NDI runtime was not found (searched {searched} and the system's library path"
            + (refused.Count > 0 ? "; " + string.Join("; ", refused) : "")
            + $") — install the NDI 6 runtime from {Link} ({RedistLink}) and set {DirVariable} to its folder";
        return null;
    }

    /// <summary>The numbers of a version string ("NDI SDK LINUX 6.3.2 …" → 6.3.2).</summary>
    public static Version ParseVersion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = VersionPattern().Match(text);
        if (!m.Success)
            return new Version(0, 0, 0);
        var major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(major, minor, patch);
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionPattern();

    /// <summary>Binds every export; the name of the first required one missing, or null.</summary>
    private string? Bind()
    {
        nint Req(string name, ref string? missing)
        {
            if (NativeLibrary.TryGetExport(Handle, name, out var p))
                return p;
            missing ??= name;
            return 0;
        }
        nint Opt(string name) => NativeLibrary.TryGetExport(Handle, name, out var p) ? p : 0;
        string? missing = null;
        Initialize = (delegate* unmanaged[Cdecl]<byte>)Req("NDIlib_initialize", ref missing);
        Destroy = (delegate* unmanaged[Cdecl]<void>)Req("NDIlib_destroy", ref missing);
        VersionString = (delegate* unmanaged[Cdecl]<nint>)Req("NDIlib_version", ref missing);
        IsSupportedCpu = (delegate* unmanaged[Cdecl]<byte>)Req("NDIlib_is_supported_CPU", ref missing);
        FindCreate = (delegate* unmanaged[Cdecl]<NdiFindCreate*, nint>)Req("NDIlib_find_create_v2", ref missing);
        FindDestroy = (delegate* unmanaged[Cdecl]<nint, void>)Req("NDIlib_find_destroy", ref missing);
        FindWaitForSources = (delegate* unmanaged[Cdecl]<nint, uint, byte>)Req("NDIlib_find_wait_for_sources", ref missing);
        FindGetCurrentSources = (delegate* unmanaged[Cdecl]<nint, uint*, NdiSourceT*>)Req("NDIlib_find_get_current_sources", ref missing);
        RecvCreate = (delegate* unmanaged[Cdecl]<NdiRecvCreate*, nint>)Req("NDIlib_recv_create_v3", ref missing);
        RecvDestroy = (delegate* unmanaged[Cdecl]<nint, void>)Req("NDIlib_recv_destroy", ref missing);
        var capture = Opt("NDIlib_recv_capture_v3");
        if (capture == 0)
            capture = Req("NDIlib_recv_capture_v2", ref missing);
        RecvCapture = (delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void*, NdiMetadataFrame*, uint, int>)capture;
        RecvFreeVideo = (delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void>)Req("NDIlib_recv_free_video_v2", ref missing);
        RecvFreeMetadata = (delegate* unmanaged[Cdecl]<nint, NdiMetadataFrame*, void>)Req("NDIlib_recv_free_metadata", ref missing);
        RecvSetTally = (delegate* unmanaged[Cdecl]<nint, NdiTally*, byte>)Req("NDIlib_recv_set_tally", ref missing);
        RecvGetNoConnections = (delegate* unmanaged[Cdecl]<nint, int>)Req("NDIlib_recv_get_no_connections", ref missing);
        SendCreate = (delegate* unmanaged[Cdecl]<NdiSendCreate*, nint>)Req("NDIlib_send_create", ref missing);
        SendDestroy = (delegate* unmanaged[Cdecl]<nint, void>)Req("NDIlib_send_destroy", ref missing);
        SendVideoAsync = (delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void>)Req("NDIlib_send_send_video_async_v2", ref missing);
        SendVideo = (delegate* unmanaged[Cdecl]<nint, NdiVideoFrame*, void>)Req("NDIlib_send_send_video_v2", ref missing);
        SendGetTally = (delegate* unmanaged[Cdecl]<nint, NdiTally*, uint, byte>)Req("NDIlib_send_get_tally", ref missing);
        SendGetNoConnections = (delegate* unmanaged[Cdecl]<nint, uint, int>)Req("NDIlib_send_get_no_connections", ref missing);
        SendGetSourceName = (delegate* unmanaged[Cdecl]<nint, NdiSourceT*>)Opt("NDIlib_send_get_source_name");
        SendAdvertiserCreate = (delegate* unmanaged[Cdecl]<NdiAdvertiserCreate*, nint>)Opt("NDIlib_send_advertiser_create");
        SendAdvertiserDestroy = (delegate* unmanaged[Cdecl]<nint, void>)Opt("NDIlib_send_advertiser_destroy");
        SendAdvertiserAddSender = (delegate* unmanaged[Cdecl]<nint, nint, byte, byte>)Opt("NDIlib_send_advertiser_add_sender");
        SendAdvertiserDelSender = (delegate* unmanaged[Cdecl]<nint, nint, byte>)Opt("NDIlib_send_advertiser_del_sender");
        RecvAdvertiserCreate = (delegate* unmanaged[Cdecl]<NdiAdvertiserCreate*, nint>)Opt("NDIlib_recv_advertiser_create");
        RecvAdvertiserDestroy = (delegate* unmanaged[Cdecl]<nint, void>)Opt("NDIlib_recv_advertiser_destroy");
        RecvAdvertiserAddReceiver = (delegate* unmanaged[Cdecl]<nint, nint, byte, byte, nint, byte>)Opt("NDIlib_recv_advertiser_add_receiver");
        RecvAdvertiserDelReceiver = (delegate* unmanaged[Cdecl]<nint, nint, byte>)Opt("NDIlib_recv_advertiser_del_receiver");
        if (SendAdvertiserAddSender == null || SendAdvertiserDestroy == null || SendAdvertiserDelSender == null)
            SendAdvertiserCreate = null;
        if (RecvAdvertiserAddReceiver == null || RecvAdvertiserDestroy == null || RecvAdvertiserDelReceiver == null)
            RecvAdvertiserCreate = null;
        return missing;
    }

    /// <summary>The optional exports this runtime has, for STATE.</summary>
    public IReadOnlyList<string> Features()
    {
        var list = new List<string>();
        if (SendGetSourceName != null)
            list.Add("sourcename");
        if (HasAdvertisers)
            list.Add("advertisers");
        return list;
    }
}
