using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Show;

/// <summary>Who holds a blind draft: the session by its door and its id (two doors may number their sessions alike), and its name.</summary>
public sealed record DraftOwner(string Transport, string SessionId, string Name)
{
    public static DraftOwner Of(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new DraftOwner(session.Transport, session.SessionId, session.ClientName);
    }

    public bool Is(SessionInfo session) =>
        string.Equals(session.SessionId, SessionId, StringComparison.Ordinal) && string.Equals(session.Transport, Transport, StringComparison.Ordinal);
}

/// <summary>A session's blind draft: a second store seeded with the live root at BLIND ON (sharing every branch), whose edits never
/// reach the air until BLIND COMMIT lands them as one undo step. Written on the lane only; every other thread reads the published
/// references (each swapped whole, never torn).</summary>
public sealed class BlindDraft(DraftOwner owner, long sinceUnixMs, ProjectStore store, ProjectSnapshot baseRoot)
{
    private volatile DraftOwner _owner = owner;
    private volatile ProjectPatch _patch = ProjectDiff.Between(baseRoot.Project, baseRoot.Project, baseRoot.Revision);
    private volatile BlindCommit? _commit;

    /// <summary>The session whose document edits go to the draft (a desk that reconnects takes its draft over once the old session is gone).</summary>
    public DraftOwner Owner
    {
        get => _owner;
        set => _owner = value;
    }

    public string OwnerName => _owner.Name;

    public long SinceUnixMs { get; } = sinceUnixMs;

    public ProjectStore Store { get; } = store;

    /// <summary>The live root the draft started from, and its revision.</summary>
    public ProjectSnapshot Base { get; } = baseRoot;

    /// <summary>The draft against its base, made again when the draft moves (lane).</summary>
    public ProjectPatch Patch
    {
        get => _patch;
        set => _patch = value;
    }

    /// <summary>A commit waiting for its boundary, or null.</summary>
    public BlindCommit? Commit
    {
        get => _commit;
        set => _commit = value;
    }
}

/// <summary>How a blind commit picks its instant.</summary>
public enum CommitBoundary { Now, At, Next, OnGo }

/// <summary>A blind commit waiting for its boundary: the patch it lands, the root it publishes (the live root with the patch put in,
/// made again whenever the live root moves), the leader instant it takes effect (long.MaxValue while it waits for a GO or a
/// switch), who asked and whether that was an operator (an armed show lands only an operator's commit), and whether a GO has pinned a
/// NEXT commit's instant.</summary>
public sealed record BlindCommit(ProjectPatch Patch, CommitBoundary Boundary, string? PlaylistId, long EffectiveLeaderNs, string By,
    bool Forced, Project Root, Project LiveAt)
{
    /// <summary>The commit was asked by an operator (or the node itself).</summary>
    public bool ByOperator { get; init; }

    /// <summary>A GO on the playlist fixed a NEXT commit's instant (the lane's next switch is no longer read).</summary>
    public bool Pinned { get; init; }

    public string Label => Patch.Changes.Count == 1 ? "blind commit: 1 change" : $"blind commit: {Patch.Changes.Count} changes";

    public string BoundaryWord => Boundary switch
    {
        CommitBoundary.Now => "now",
        CommitBoundary.At => "at",
        CommitBoundary.Next => "next",
        _ => "on go",
    };
}
