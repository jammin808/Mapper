using System.Text.Json;

namespace PlayMap.Engine.Tests;

/// <summary>How a fixture's text is written: indented, lines ended by "\n" on every platform, as the committed files are — a
/// fixture never depends on the machine that wrote it.</summary>
internal static class FixtureJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };
}
