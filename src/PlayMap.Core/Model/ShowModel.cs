using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>What PANIC does on one output.</summary>
public enum PanicOutcome { Black, Run, Rewind, Next, Freeze, Safe, Shutter, FxOff, Hard }

/// <summary>A lock names one entity by id, or an area by name (<see cref="LockAreas"/>).</summary>
public enum LockKind { Entity, Area }

/// <summary>The areas a lock may cover, by name (an armed show adds mapping and layout implicitly).</summary>
public static class LockAreas
{
    public const string Mapping = "mapping", Layout = "layout", Content = "content", Playlists = "playlists",
        Media = "media", Devices = "devices", Network = "network";
    public static readonly IReadOnlyList<string> All = [Mapping, Layout, Content, Playlists, Media, Devices, Network];
}

/// <summary>What PANIC does on one output: the outcome, its fade, and the safe look SAFE dips to.</summary>
public sealed record OutputPanicPlan(string OutputId, PanicOutcome Outcome, int FadeMs, SourceRef? SafeLook);

/// <summary>A lock on an entity id or an area name, who set it, when and why.</summary>
public sealed record ShowLock(string Target, LockKind Kind, string By, long AtUnixMs, string Words);

/// <summary>The show's rules for Show Mode, GO protection, panic and blind commits.</summary>
public sealed record ShowRules
{
    public static readonly ShowRules Default = new();
    public int MinGoMsArmed { get; init; } = 300;
    public int MinGoMsRehearsal { get; init; }
    /// <summary>Armed, paired remote clients may only read (QLab's "view only in Show Mode").</summary>
    public bool RemoteViewOnlyWhenArmed { get; init; }
    /// <summary>"paired": any paired operator or remote may DISARM; "desk": the desk only.</summary>
    public string DisarmFrom { get; init; } = "paired";
    public PanicOutcome DefaultOutcome { get; init; } = PanicOutcome.Black;
    public int DefaultFadeMs { get; init; } = 1000;
    /// <summary>A panic and a blind commit land this far ahead on the show clock, so mirrors and followers fade on the same frames.</summary>
    public int PanicLeadMs { get; init; }
    public int CommitLeadMs { get; init; } = 34;
    /// <summary>Warn (amber) when a playlist plays while the show is not armed.</summary>
    public bool ExpectArmed { get; init; }
}

/// <summary>The show's Show Mode rules, its panic plan per output and its locks (wp2-show).</summary>
public sealed record ShowPlan
{
    public static readonly ShowPlan Default = new();
    public ShowRules Rules { get; init; } = ShowRules.Default;
    public ImmutableArray<OutputPanicPlan> Panic { get; init; } = [];
    public ImmutableArray<ShowLock> Locks { get; init; } = [];
}
