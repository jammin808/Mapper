using PlayMap.Media.Audio;
using PlayMap.Media.Audio.Windows;

namespace PlayMap.Engine.Audio;

/// <summary>This machine's sound backends, chosen by the node's setting ("auto", "ffmpeg", "wasapi", "none"): WASAPI first on
/// Windows (its inputs, the system's sound, everything but PlayMap), FFmpeg's capture devices (pulse, oss, openal on Linux; dshow
/// on Windows), and sound files. The test inputs are always there.</summary>
internal static class AudioBackends
{
    public static void AddTo(AudioRuntime runtime, string choice)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var c = (choice ?? "auto").Trim().ToLowerInvariant();
        if (c == "none")
            return;
        if (OperatingSystem.IsWindows() && c is "auto" or "wasapi")
            runtime.AddBackend(WasapiAudioInputs.Prefix, new WasapiAudioInputs());
        if (c is "auto" or "ffmpeg")
        {
            var ffmpeg = new FfmpegAudioInputs();
            foreach (var format in FfmpegAudioInputs.PlatformFormats)
                runtime.AddBackend(format + ":", ffmpeg);
        }
        runtime.AddBackend(FileAudioInputs.Prefix, new FileAudioInputs());
    }
}
