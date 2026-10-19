using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PlayMap.Media.Ffmpeg;

/// <summary>The FFmpeg shared libraries could not be used: missing, the wrong major version, or failing to load.
/// The message names every folder searched and what was wrong in each.</summary>
public sealed class FfmpegUnavailableException : Exception
{
    public FfmpegUnavailableException(string message) : base(message)
    {
    }

    public FfmpegUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }

    public FfmpegUnavailableException()
    {
    }
}

/// <summary>What the loader found: the folder, the libraries' versions and build string, and which optional
/// libraries (filters, devices) are missing.</summary>
public sealed record FfmpegStatus(
    string Folder,
    string Build,
    int AvCodecMajor,
    int AvFormatMajor,
    int AvUtilMajor,
    bool DevicesAvailable,
    bool FiltersAvailable);

/// <summary>Finds and loads FFmpeg's shared libraries once per process. The folder is, in order: the
/// <c>PLAYMAP_FFMPEG_DIR</c> environment variable, the exe folder's <c>ffmpeg</c> subfolder, the exe folder
/// (each may also hold the libraries in a <c>lib</c> or <c>bin</c> subfolder). The libraries are loaded by full
/// path in dependency order (avutil, swresample, swscale, avcodec, avformat, avfilter, avdevice) so no search
/// path is consulted and a dependency always resolves to the copy beside it. The binding's structs change
/// layout between majors, so a library whose major differs from the binding's is refused, never used.</summary>
public static class FfmpegLoader
{
    public const string EnvironmentVariable = "PLAYMAP_FFMPEG_DIR";

    // Dependency order: each library's dependencies come before it.
    private static readonly (string Name, bool Required)[] Libraries =
    [
        ("avutil", true),
        ("swresample", true),
        ("swscale", true),
        ("avcodec", true),
        ("avformat", true),
        ("avfilter", false),
        ("avdevice", false),
    ];

    private static readonly Lock Gate = new();
    private static FfmpegStatus? _status;
    private static string? _failure;
    private static readonly Dictionary<string, nint> Handles = new(StringComparer.Ordinal);

    /// <summary>The binding's expected major version of a library ("avcodec" → 63).</summary>
    public static int ExpectedMajor(string library) =>
        ffmpeg.LibraryVersionMap.TryGetValue(library, out var major) ? major : -1;

    /// <summary>The loaded state, or null before a successful load.</summary>
    public static FfmpegStatus? Status
    {
        get
        {
            lock (Gate)
                return _status;
        }
    }

    /// <summary>Loads the libraries if not yet loaded; throws <see cref="FfmpegUnavailableException"/> with the
    /// reason when they cannot be used. A failure is remembered: a process does not retry a broken install on
    /// every clip.</summary>
    public static FfmpegStatus EnsureLoaded()
    {
        lock (Gate)
        {
            if (_status is not null)
                return _status;
            if (_failure is not null)
                throw new FfmpegUnavailableException(_failure);
            try
            {
                _status = Load();
                return _status;
            }
            catch (FfmpegUnavailableException e)
            {
                _failure = e.Message;
                throw;
            }
        }
    }

    /// <summary>Loads the libraries; false with the reason when they cannot be used.</summary>
    public static bool TryLoad(out string problem)
    {
        try
        {
            EnsureLoaded();
            problem = "";
            return true;
        }
        catch (FfmpegUnavailableException e)
        {
            problem = e.Message;
            return false;
        }
    }

    /// <summary>The folders searched, in order (existing or not).</summary>
    public static IReadOnlyList<string> CandidateFolders()
    {
        var list = new List<string>();
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            list.Add(Path.GetFullPath(env.Trim()));
        var exe = AppContext.BaseDirectory;
        list.Add(Path.GetFullPath(Path.Combine(exe, "ffmpeg")));
        list.Add(Path.GetFullPath(exe));
        return list;
    }

    /// <summary>The platform's file name of a library at a major version: avcodec-63.dll, libavcodec.so.63,
    /// libavcodec.63.dylib.</summary>
    public static string LibraryFileName(string library, int major)
    {
        if (OperatingSystem.IsWindows())
            return string.Create(CultureInfo.InvariantCulture, $"{library}-{major}.dll");
        if (OperatingSystem.IsMacOS())
            return string.Create(CultureInfo.InvariantCulture, $"lib{library}.{major}.dylib");
        return string.Create(CultureInfo.InvariantCulture, $"lib{library}.so.{major}");
    }

    /// <summary>The entry points a playing source first calls at a loop point or a seek, which can come at any frame of a show.
    /// The binding binds an entry point on its first call and allocates as it does, on the calling thread; these are bound as the
    /// libraries load, so a frame thread's first seek allocates nothing.</summary>
    internal static readonly IReadOnlyList<string> SeekPath = ["avformat_seek_file", "av_seek_frame", "avcodec_flush_buffers"];

    private static void BindSeekPath(string folder)
    {
        vectors.avformat_seek_file = Bind<vectors.avformat_seek_file_delegate>(folder, "avformat", "avformat_seek_file");
        vectors.av_seek_frame = Bind<vectors.av_seek_frame_delegate>(folder, "avformat", "av_seek_frame");
        vectors.avcodec_flush_buffers = Bind<vectors.avcodec_flush_buffers_delegate>(folder, "avcodec", "avcodec_flush_buffers");
    }

    private static T Bind<T>(string folder, string library, string name) =>
        DynamicallyLoadedBindings.FunctionResolver.GetFunctionDelegate<T>(library, name, false)
        ?? throw new FfmpegUnavailableException($"The FFmpeg libraries in {folder} loaded but {name} could not be bound.");

    /// <summary>A loaded library's handle (for exports the binding does not wrap), or zero.</summary>
    internal static nint Handle(string library)
    {
        lock (Gate)
            return Handles.TryGetValue(library, out var h) ? h : 0;
    }

    private static FfmpegStatus Load()
    {
        var problems = new List<string>();
        string? folder = null;
        foreach (var candidate in CandidateFolders())
        {
            foreach (var dir in new[] { candidate, Path.Combine(candidate, "lib"), Path.Combine(candidate, "bin") })
            {
                var why = InspectFolder(dir);
                if (why is null)
                {
                    folder = dir;
                    break;
                }
                problems.Add(dir + ": " + why);
            }
            if (folder is not null)
                break;
        }
        if (folder is null)
        {
            throw new FfmpegUnavailableException(
                string.Create(CultureInfo.InvariantCulture, $"FFmpeg {ExpectedMajor("avcodec")}.x shared libraries (avcodec {ExpectedMajor("avcodec")}, avformat {ExpectedMajor("avformat")}, avutil {ExpectedMajor("avutil")}) were not found. Set {EnvironmentVariable} to their folder, or put them in the exe's ffmpeg folder. Searched: ")
                + string.Join("; ", problems));
        }

        var optionalMissing = new List<string>();
        foreach (var (name, required) in Libraries)
        {
            var path = Path.Combine(folder, LibraryFileName(name, ExpectedMajor(name)));
            if (!File.Exists(path))
            {
                if (required)
                    throw new FfmpegUnavailableException($"{path} is missing.");
                optionalMissing.Add(name);
                continue;
            }
            if (!NativeLibrary.TryLoad(path, out var handle))
            {
                if (required)
                    throw new FfmpegUnavailableException($"{path} exists but did not load (a library it needs is missing from the system).");
                optionalMissing.Add(name);
                continue;
            }
            Handles[name] = handle;
        }

        ffmpeg.RootPath = folder;
        DynamicallyLoadedBindings.Initialize();

        int codecMajor, formatMajor, utilMajor;
        string build;
        try
        {
            codecMajor = (int)(ffmpeg.avcodec_version() >> 16);
            formatMajor = (int)(ffmpeg.avformat_version() >> 16);
            utilMajor = (int)(ffmpeg.avutil_version() >> 16);
            build = ffmpeg.av_version_info() ?? "";
        }
        catch (NotSupportedException e)
        {
            throw new FfmpegUnavailableException($"The FFmpeg libraries in {folder} loaded but their entry points could not be bound.", e);
        }
        if (codecMajor != ExpectedMajor("avcodec") || formatMajor != ExpectedMajor("avformat") || utilMajor != ExpectedMajor("avutil"))
        {
            throw new FfmpegUnavailableException(string.Create(CultureInfo.InvariantCulture,
                $"The FFmpeg libraries in {folder} report avcodec {codecMajor}, avformat {formatMajor}, avutil {utilMajor}; this build needs avcodec {ExpectedMajor("avcodec")}, avformat {ExpectedMajor("avformat")}, avutil {ExpectedMajor("avutil")}."));
        }
        BindSeekPath(folder);

        var devices = !optionalMissing.Contains("avdevice");
        if (devices)
            ffmpeg.avdevice_register_all();
        FfmpegLog.Install();
        return new FfmpegStatus(folder, build, codecMajor, formatMajor, utilMajor, devices, !optionalMissing.Contains("avfilter"));
    }

    /// <summary>Null when the folder holds the required libraries at the binding's majors; otherwise what is
    /// wrong (missing, or present at another major).</summary>
    internal static string? InspectFolder(string dir)
    {
        if (!Directory.Exists(dir))
            return "no such folder";
        foreach (var (name, required) in Libraries)
        {
            if (!required)
                continue;
            var expected = ExpectedMajor(name);
            if (File.Exists(Path.Combine(dir, LibraryFileName(name, expected))))
                continue;
            var other = FindOtherMajor(dir, name);
            return other is null
                ? $"{LibraryFileName(name, expected)} missing"
                : $"{other} is the wrong major version (this build needs {LibraryFileName(name, expected)})";
        }
        return null;
    }

    private static string? FindOtherMajor(string dir, string name)
    {
        var pattern = OperatingSystem.IsWindows() ? name + "-*.dll"
            : OperatingSystem.IsMacOS() ? "lib" + name + ".*.dylib"
            : "lib" + name + ".so.*";
        var file = Directory.EnumerateFiles(dir, pattern).FirstOrDefault();
        return file is null ? null : Path.GetFileName(file);
    }
}
