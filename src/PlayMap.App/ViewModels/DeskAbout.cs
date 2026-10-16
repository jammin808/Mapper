using PlayMap.Core;

namespace PlayMap.App.ViewModels;

/// <summary>The desk's own build in the words it shows, from <see cref="BuildInfo.Current"/>: the window's title starts with the short
/// form ("PlayMap 0.2.0 · Show") and the help's About line names the whole build ("PlayMap 0.2.0 (build 17, abc1234)"). A node's
/// build is its HEALTH "build"; the desk shows its own.</summary>
public static class DeskAbout
{
    /// <summary>The title's format: the short form, then the project's name in place of {0}.</summary>
    public static string TitleFormat { get; } = BuildInfo.Current.Short + " · {0}";

    /// <summary>The About line.</summary>
    public static string Build { get; } = BuildInfo.Current.Display;
}
