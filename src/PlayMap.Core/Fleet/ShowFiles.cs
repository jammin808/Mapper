using PlayMap.Core.Model;

namespace PlayMap.Core.Fleet;

/// <summary>A file a show names beside its project folder that its stage or its picture reads, as a backup pulls it from its primary:
/// the key the primary serves it under (its kind and its entity's id — the same on every node holding the same show), its path
/// relative to the project folder, and the hash the show checks it by: a mesh's "sha256:…", a measured map's own short hash (the first
/// 16 hex of its SHA-256), or "" for a survey's thinned cloud (written under a fresh name at each import, so its path is its identity).</summary>
public sealed record ShowFile(string Key, string Path, string Hash);

/// <summary>The files a show names beside it, pure: each stage mesh read from a file (a survey's mesh and a scan's among them), each
/// output's measured warp and blend map, and each survey's thinned cloud. Media and their optimised versions are not among them: they
/// are pulled by their content hash into the media store.</summary>
public static class ShowFiles
{
    public static IReadOnlyList<ShowFile> Of(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var files = new List<ShowFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string key, string path, string hash)
        {
            if (Beside(path) && seen.Add(path))
                files.Add(new ShowFile(key, path, hash));
        }
        foreach (var m in project.Stage.Meshes)
        {
            if (m.Origin != MeshOrigin.Primitive && m.Path.Length > 0)
                Add("mesh/" + m.Id, m.Path, m.ContentHash);
        }
        foreach (var o in project.Outputs)
        {
            if (o.Measured?.Warp is { } warp)
                Add("warp/" + o.Id, warp.File, warp.Hash);
            if (o.Measured?.Blend is { } blend)
                Add("blend/" + o.Id, blend.File, blend.Hash);
        }
        foreach (var s in project.Stage.Surveys)
        {
            if (s.CloudPath.Length > 0)
                Add("cloud/" + s.Id, s.CloudPath, "");
        }
        return files;
    }

    /// <summary>A path a show may name beside it: relative, each part a name (no root, no drive, no "." or "..", no empty part).</summary>
    public static bool Beside(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains(':', StringComparison.Ordinal) || System.IO.Path.IsPathRooted(path))
            return false;
        foreach (var part in path.Split('/', '\\'))
        {
            if (part.Length == 0 || part is "." or "..")
                return false;
        }
        return true;
    }

    /// <summary>Whether bytes whose SHA-256 is <paramref name="sha256Hex"/> are the file a show names by <paramref name="hash"/>: the
    /// whole digest ("sha256:…" or bare), its leading hex (a measured map's short hash), or any file for a show that names none.</summary>
    public static bool Matches(string sha256Hex, string hash)
    {
        ArgumentNullException.ThrowIfNull(sha256Hex);
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length == 0)
            return true;
        if (sha256Hex.Length != 64)
            return false;
        if (hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(hash[7..], sha256Hex, StringComparison.OrdinalIgnoreCase);
        return hash.Length is >= 16 and <= 64 && sha256Hex.StartsWith(hash, StringComparison.OrdinalIgnoreCase);
    }
}
