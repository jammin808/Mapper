using PlayMap.Core.Model;
using PlayMap.Core.Show;

namespace PlayMap.Engine.Show;

/// <summary>A session's blind draft: a second store seeded with the live root at BLIND ON (sharing every branch), whose edits never
/// reach the air until BLIND COMMIT lands them as one undo step. Lane-only writes; readers take the published values.</summary>
public sealed class BlindDraft(string ownerSession, string ownerName, long sinceUnixMs, ProjectStore store, ProjectSnapshot baseRoot)
{
    /// <summary>The session whose document edits go to the draft (a desk that reconnects takes its draft over by name).</summary>
    public string OwnerSession { get; set; } = ownerSession;

    public string OwnerName { get; } = ownerName;

    public long SinceUnixMs { get; } = sinceUnixMs;

    public ProjectStore Store { get; } = store;

    /// <summary>The live root the draft started from, and its revision.</summary>
    public ProjectSnapshot Base { get; } = baseRoot;

    /// <summary>The draft against its base, made again when the draft moves (lane).</summary>
    public ProjectPatch Patch { get; set; } = ProjectDiff.Between(baseRoot.Project, baseRoot.Project, baseRoot.Revision);

    /// <summary>A commit waiting for its boundary, or null.</summary>
    public BlindCommit? Commit { get; set; }
}

/// <summary>How a blind commit picks its instant.</summary>
public enum CommitBoundary { Now, At, Next, OnGo }

/// <summary>A blind commit waiting for its boundary: the patch it lands, the root it publishes (the live root with the patch put in,
/// made again whenever the live root moves), the leader instant it takes effect (long.MaxValue while it waits for a GO), who asked
/// and how.</summary>
public sealed record BlindCommit(ProjectPatch Patch, CommitBoundary Boundary, string? PlaylistId, long EffectiveLeaderNs, string By,
    bool Forced, Project Root, Project LiveAt)
{
    public string Label => Patch.Changes.Count == 1 ? "blind commit: 1 change" : $"blind commit: {Patch.Changes.Count} changes";

    public string BoundaryWord => Boundary switch
    {
        CommitBoundary.Now => "now",
        CommitBoundary.At => "at",
        CommitBoundary.Next => "next",
        _ => "on go",
    };
}
