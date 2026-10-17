using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PlayMap.Media.Video.Ndi;

namespace PlayMap.Media.Tests.Video;

/// <summary>What the fake runtime counted (fake_ndi_stats_t in tests/native/fake_ndi.c, field for field).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FakeNdiStats
{
    public long Initialized;
    public long FindsCreated;
    public long SendsCreated;
    public long RecvsCreated;
    public long FramesSent;
    public long LifetimeViolations;
    public long SendAdvertisers;
    public long RecvAdvertisers;
    public long SendersAdvertised;
    public long ReceiversAdvertised;
    public long LastRecvColorFormat;
    public long LastRecvBandwidth;
    public long LastSendClockVideo;
    public long VideoCaptured;
}

/// <summary>PlayMap's own test double of the NDI runtime (tests/native/fake_ndi.c, its layouts transcribed from NDI's MIT-licensed
/// headers), compiled here into the runtime's file name — libndi.so.6 with cc on Linux, Processing.NDI.Lib.x64.dll with the runner's
/// clang on Windows (<see cref="NativeBuild"/>) — once per source version, into a folder of the temp directory, and driven through
/// its fake_ndi_* exports. Two copies are built in two folders, each its own library with its own state: one stays at its default
/// version (6.3), the other is told it is 6.2 before PlayMap loads it. No NDI binary is involved.</summary>
internal static unsafe class FakeNdi
{
    private static readonly Lazy<(string? Dir, string Why)> Built = new(BuildOnce);

    public static string? Dir => Built.Value.Dir;

    public static string Why => Built.Value.Why;

    /// <summary>The folder of the 6.3 copy.</summary>
    public static string Folder63 => Path.Combine(Built.Value.Dir ?? throw new InvalidOperationException(Why), "v63");

    /// <summary>The folder of the copy that says 6.2.</summary>
    public static string Folder62 => Path.Combine(Built.Value.Dir ?? throw new InvalidOperationException(Why), "v62");

    public static string Lib(string folder) => Path.Combine(folder, NdiRuntime.FileName);

    private static (string? Dir, string Why) BuildOnce()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return (null, "the fake NDI runtime is built on Linux (cc) and Windows (clang)");
        var source = Path.Combine(VmxBuild.RepoRoot(), "tests", "native", "fake_ndi.c");
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)))[..12];
        var dir = Path.Combine(Path.GetTempPath(), "playmap-fakendi-" + hash);
        foreach (var v in new[] { "v63", "v62" })
        {
            var folder = Path.Combine(dir, v);
            var lib = Lib(folder);
            if (File.Exists(lib))
                continue;
            Directory.CreateDirectory(folder);
            // Built under a name of its own, then moved: another test process building the same source never loads half a file.
            var partial = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"building-{Environment.ProcessId}-{NdiRuntime.FileName}"));
            string[] args = OperatingSystem.IsWindows()
                ? ["-O1", "-std=c11", "-Wall", "-shared", source, "-o", partial]
                : ["-O1", "-std=c11", "-Wall", "-fPIC", "-shared", "-pthread", source, "-o", partial];
            if (NativeBuild.Run(NativeBuild.C, args, partial, "the fake NDI runtime") is { Length: > 0 } why)
                return (null, why);
            File.Move(partial, lib, overwrite: true);
        }
        return (dir, "");
    }

    /// <summary>The control exports of the copy in a folder.</summary>
    public sealed class Control
    {
        private readonly delegate* unmanaged[Cdecl]<nint, void> _setVersion;
        private readonly delegate* unmanaged[Cdecl]<void> _reset;
        private readonly delegate* unmanaged[Cdecl]<nint, nint, int> _injectSource;
        private readonly delegate* unmanaged[Cdecl]<nint, int, int, int, void*, int, long, nint, byte> _injectFrame;
        private readonly delegate* unmanaged[Cdecl]<FakeNdiStats*, void> _stats;
        private readonly delegate* unmanaged[Cdecl]<int, byte*, int, void> _strings;
        private readonly delegate* unmanaged[Cdecl]<long*, int, void> _layout;
        private readonly delegate* unmanaged[Cdecl]<int, void> _injectFormat;

        public Control(string folder)
        {
            var handle = NativeLibrary.Load(Lib(folder));
            _setVersion = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(handle, "fake_ndi_set_version");
            _reset = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(handle, "fake_ndi_reset");
            _injectSource = (delegate* unmanaged[Cdecl]<nint, nint, int>)NativeLibrary.GetExport(handle, "fake_ndi_inject_source");
            _injectFrame = (delegate* unmanaged[Cdecl]<nint, int, int, int, void*, int, long, nint, byte>)NativeLibrary.GetExport(handle, "fake_ndi_inject_frame");
            _stats = (delegate* unmanaged[Cdecl]<FakeNdiStats*, void>)NativeLibrary.GetExport(handle, "fake_ndi_stats");
            _strings = (delegate* unmanaged[Cdecl]<int, byte*, int, void>)NativeLibrary.GetExport(handle, "fake_ndi_strings");
            _layout = (delegate* unmanaged[Cdecl]<long*, int, void>)NativeLibrary.GetExport(handle, "fake_ndi_layout");
            _injectFormat = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(handle, "fake_ndi_set_inject_format");
        }

        public void Reset() => _reset();

        /// <summary>The frame_format_type of the frames injected from now on (1 progressive, 0 interleaved, 2 field 0, 3 field 1).</summary>
        public void InjectFormat(int frameFormatType) => _injectFormat(frameFormatType);

        public void SetVersion(string? version)
        {
            var p = version is null ? 0 : Marshal.StringToCoTaskMemUTF8(version);
            try
            {
                _setVersion(p);
            }
            finally
            {
                if (p != 0)
                    Marshal.FreeCoTaskMem(p);
            }
        }

        public bool InjectSource(string fullName, string url)
        {
            var n = Marshal.StringToCoTaskMemUTF8(fullName);
            var u = Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                return _injectSource(n, u) == 1;
            }
            finally
            {
                Marshal.FreeCoTaskMem(n);
                Marshal.FreeCoTaskMem(u);
            }
        }

        public bool InjectFrame(string fullName, int fourCc, int width, int height, byte[] data, int stride, long timestamp100Ns, string? metadata = null)
        {
            var n = Marshal.StringToCoTaskMemUTF8(fullName);
            var m = metadata is null ? 0 : Marshal.StringToCoTaskMemUTF8(metadata);
            try
            {
                fixed (byte* d = data)
                    return _injectFrame(n, fourCc, width, height, d, stride, timestamp100Ns, m) != 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem(n);
                if (m != 0)
                    Marshal.FreeCoTaskMem(m);
            }
        }

        public FakeNdiStats Stats()
        {
            FakeNdiStats s;
            _stats(&s);
            return s;
        }

        /// <summary>0 last find groups, 1 last find extra IPs, 2 last send groups, 3 discovery read from the configuration, 4 last
        /// send-advertiser URL, 5 last receiver name, 6 last tally "program,preview", 7 last recv-advertiser URL.</summary>
        public string Strings(int which)
        {
            var buf = new byte[1024];
            fixed (byte* b = buf)
                _strings(which, b, buf.Length);
            var end = Array.IndexOf(buf, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end);
        }

        public long[] Layout(int n)
        {
            var values = new long[n];
            fixed (long* v = values)
                _layout(v, n);
            return values;
        }
    }
}

/// <summary>A test against the fake NDI runtime: skipped, with the reason, only where it cannot be built.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class NdiFactAttribute : FactAttribute
{
    public NdiFactAttribute()
    {
        // Where the doubles are required (the CI jobs) a runtime that would not build fails each test with the words instead.
        if (FakeNdi.Dir is null && !NativeBuild.Required)
            Skip = FakeNdi.Why;
    }
}
