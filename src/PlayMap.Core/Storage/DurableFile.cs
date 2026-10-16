namespace PlayMap.Core.Storage;

/// <summary>A file written beside its place and renamed over it reaches the disk before the rename: a power cut after the rename finds
/// the new file whole, never a name pointing at blocks the disk never got (a journalling file system may commit the rename before
/// the data). Every file the node renames into place goes through <see cref="MoveIntoPlace"/>.</summary>
public static class DurableFile
{
    /// <summary>Raised after a file's bytes reached the disk and before it is renamed into place, with its final path (any thread).</summary>
    public static event Action<string>? Flushed;

    /// <summary>Flushes <paramref name="temp"/> to the disk, then renames it to <paramref name="path"/>.</summary>
    public static void MoveIntoPlace(string temp, string path, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(temp);
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Write access: Windows flushes a file's buffers only through a handle that may write it. Opening it does not change it.
        using (var file = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            file.Flush(flushToDisk: true);
        Flushed?.Invoke(path);
        File.Move(temp, path, overwrite);
    }

    /// <summary>Writes <paramref name="text"/> (UTF-8) beside <paramref name="path"/> and moves it into place, flushed.</summary>
    public static void WriteAllText(string path, string text, string suffix = ".saving")
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var temp = path + suffix;
        File.WriteAllText(temp, text);
        MoveIntoPlace(temp, path);
    }

    /// <summary>Writes <paramref name="bytes"/> beside <paramref name="path"/> and moves it into place, flushed.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, string suffix = ".saving")
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var temp = path + suffix;
        File.WriteAllBytes(temp, bytes);
        MoveIntoPlace(temp, path);
    }
}
