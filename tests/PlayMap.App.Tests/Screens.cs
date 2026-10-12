using Avalonia.Controls;
using Avalonia.Headless;

namespace PlayMap.App.Tests;

/// <summary>Saves what a window shows to a PNG, when PLAYMAP_SCREENS_DIR names a folder (the papers'
/// screenshots are made this way); otherwise does nothing, so a test run writes no files.</summary>
public static class Screens
{
    public static string? Save(Window window, string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        var dir = Environment.GetEnvironmentVariable("PLAYMAP_SCREENS_DIR");
        if (string.IsNullOrEmpty(dir))
            return null;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
            return null;
        var path = Path.Combine(dir, name + ".png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions { CompressionLevel = System.IO.Compression.CompressionLevel.SmallestSize });
        return path;
    }
}
