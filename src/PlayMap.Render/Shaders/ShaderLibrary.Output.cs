namespace PlayMap.Render.Shaders;

/// <summary>wp2-output's programs, by name (sources carry no #version and no precision line).</summary>
public static partial class ShaderLibrary
{
    static partial void AddOutput(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        // Presentation adds no program: a presenter draws the finished picture with the library's "present" program.
    }
}
