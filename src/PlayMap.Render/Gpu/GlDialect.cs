namespace PlayMap.Render.Gpu;

/// <summary>The GLSL dialect a context compiles: desktop GLSL 3.30 core (the only one this round).</summary>
public enum GlDialect
{
    Core330,
}

/// <summary>What each dialect puts in front of every program source; the sources themselves carry no header.</summary>
public static class GlDialects
{
    /// <summary>The first lines of every program for the dialect ("#version 330 core\n").</summary>
    public static string Header(GlDialect dialect) => dialect switch
    {
        GlDialect.Core330 => "#version 330 core\n",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "no header for this dialect"),
    };

    /// <summary>The dialect's name as <see cref="GlCaps.Dialect"/> and the node's settings write it.</summary>
    public static string Word(GlDialect dialect) => dialect switch
    {
        GlDialect.Core330 => "core330",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "no word for this dialect"),
    };
}
