namespace PlayMap.Core.Tests.Versions;

/// <summary>The commit a checkout's HEAD names, read from its .git without the git program: .git is the repository's folder, or a
/// file naming it ("gitdir: …", as a worktree's and a submodule's are); HEAD is a commit or "ref: refs/…", found as a loose ref in
/// the worktree's folder or the repository's common one ("commondir"), or as a line of the common folder's packed-refs. Null
/// outside a checkout and for a HEAD that names no commit yet.</summary>
internal static class GitHead
{
    public static string? Of(string root)
    {
        var gitDir = GitDir(root);
        if (gitDir is null)
            return null;
        var commonFile = Path.Combine(gitDir, "commondir");
        var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())) : gitDir;
        var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
        // A symbolic ref may name another; a chain longer than git's own limit names nothing.
        for (var hops = 0; hops < 5 && head.StartsWith("ref:", StringComparison.Ordinal); hops++)
            head = Ref(gitDir, common, head["ref:".Length..].Trim()) ?? "";
        return head.Length is 40 or 64 && head.All(char.IsAsciiHexDigit) ? head.ToLowerInvariant() : null;
    }

    private static string? GitDir(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
            return dotGit;
        if (!File.Exists(dotGit))
            return null;
        var line = File.ReadAllText(dotGit).Trim();
        return line.StartsWith("gitdir:", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()))
            : null;
    }

    private static string? Ref(string gitDir, string common, string name)
    {
        foreach (var dir in new[] { gitDir, common })
        {
            var loose = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose))
                return File.ReadAllText(loose).Trim();
        }
        var packed = Path.Combine(common, "packed-refs");
        if (!File.Exists(packed))
            return null;
        // "<sha> <ref>" lines; '#' starts the header, '^' a peeled tag's commit.
        foreach (var line in File.ReadLines(packed))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && line[0] is not ('#' or '^') && string.Equals(line[(space + 1)..].Trim(), name, StringComparison.Ordinal))
                return line[..space];
        }
        return null;
    }
}
