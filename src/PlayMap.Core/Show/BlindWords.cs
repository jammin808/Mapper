using System.Globalization;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Show;

/// <summary>The words a blind draft is spoken of in: the node that holds the draft refuses with them, and a client that greys a
/// line before it sends it shows the same words from STATE.show.blind.</summary>
public static class BlindWords
{
    /// <summary>"1 change", "3 changes".</summary>
    public static string Changes(int count) =>
        count == 1 ? "1 change" : string.Create(CultureInfo.InvariantCulture, $"{count} changes");

    /// <summary>True for a line that opens another show (PROJECT OPEN, PROJECT NEW): refused while a draft is open, since the draft
    /// and its commit would be left for a show that is gone.</summary>
    public static bool OpensAnotherShow(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Name is "PROJECT OPEN" or "PROJECT NEW";
    }

    /// <summary>Why a line that opens another show is refused while <paramref name="owner"/>'s draft of
    /// <paramref name="changes"/> changes is open.</summary>
    public static string DraftOpen(string owner, int changes) =>
        $"a blind draft is open ({owner}, {Changes(changes)}) — BLIND COMMIT or BLIND DISCARD before opening another show";

    /// <summary>Why the draft's owner's edit is refused while its commit waits for its boundary (<paramref name="when"/>: "with the
    /// next GO on Show", "at 20:31:05.120").</summary>
    public static string Landing(string when) =>
        $"the blind draft lands {when} — no more edits to it; BLIND DISCARD cancels the commit";
}
