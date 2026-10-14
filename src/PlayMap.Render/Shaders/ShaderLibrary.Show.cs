namespace PlayMap.Render.Shaders;

/// <summary>wp2-show's programs, by name (sources carry no #version and no precision line). The air's gain is the spine's
/// uAir in the output program, so the show adds none.</summary>
public static partial class ShaderLibrary
{
    static partial void AddShow(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        // No program: panic, master and the safe look reach the air through uAir and the pattern-override path.
    }
}
