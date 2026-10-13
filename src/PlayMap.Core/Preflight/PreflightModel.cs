using PlayMap.Core.Bus;

namespace PlayMap.Core.Preflight;

/// <summary>How deep a preflight looks: QUICK reads facts; FULL also decodes every clip in the show.</summary>
public enum PreflightDepth { Quick, Full }

/// <summary>One readiness row: what was checked, its light, the value and note in words, and a line that fixes it.</summary>
public sealed record PreflightRow(string Id, string Node, string Section, string Item, Light Light, string Value, string Note, string? Fix);

/// <summary>Five steps (outputs, media, mapping, sync and backup, safety); a step that does not apply counts as done and says so.</summary>
public sealed record ShowReady(int Done, int Of, string FirstOpen);

/// <summary>A preflight's rows and readiness, with when it ran and how long it took.</summary>
public sealed record PreflightReport(string Id, long AtUnixMs, PreflightDepth Depth, long DurationMs, IReadOnlyList<PreflightRow> Rows,
    ShowReady Ready, bool Complete);
