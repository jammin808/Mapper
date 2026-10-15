using System.Text.Json;

namespace PlayMap.Engine.Tests;

/// <summary>How a test writes a committed fixture's text: indented, with "\n" line ends on every machine (the
/// writer's own default is the machine's), so what a test makes on Windows is what the checkout holds.</summary>
internal static class FixtureJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };
}
