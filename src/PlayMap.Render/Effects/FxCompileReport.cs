namespace PlayMap.Render.Effects;

/// <summary>The outcome of compiling sources for a report (the program is not kept): ok, or the driver's log.</summary>
public sealed record FxCompileReport(bool Ok, string? Error, string Lane);
