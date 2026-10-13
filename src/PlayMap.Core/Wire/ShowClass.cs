namespace PlayMap.Core.Wire;

/// <summary>What a verb does to the show, for Show Mode's table (one per catalogue row; Target = classed by the id it names).</summary>
public enum ShowClass
{
    Query, Transport, Air, Panic, Show, Perform, Content, Playlists, Mapping, Layout, Media, Heavy, Setup,
    DeviceSafe, DevicePower, Project, Network, Fleet, Admin, Assistant, Target,
}

/// <summary>The show class a row has unless it says otherwise, and the classes whose risk a word may only raise.</summary>
public static class ShowClasses
{
    /// <summary>The class a verb has unless its row says otherwise.</summary>
    public static ShowClass Default(VerbCategory category, bool mutates) => !mutates ? ShowClass.Query : category switch
    {
        VerbCategory.Session => ShowClass.Query,
        VerbCategory.Transport => ShowClass.Transport,
        VerbCategory.Playlist => ShowClass.Playlists,
        VerbCategory.Media => ShowClass.Media,
        VerbCategory.Content or VerbCategory.Effects or VerbCategory.Modulation or VerbCategory.Video => ShowClass.Content,
        VerbCategory.Output or VerbCategory.Led => ShowClass.Layout,
        VerbCategory.Mapping or VerbCategory.Site or VerbCategory.Align or VerbCategory.Calibration => ShowClass.Mapping,
        VerbCategory.Scan => ShowClass.Heavy,
        VerbCategory.Project => ShowClass.Project,
        VerbCategory.Network => ShowClass.Network,
        VerbCategory.Fleet => ShowClass.Network,
        VerbCategory.Device => ShowClass.DeviceSafe,
        VerbCategory.Assistant => ShowClass.Assistant,
        VerbCategory.Show => ShowClass.Show,
        VerbCategory.Control or VerbCategory.Timecode or VerbCategory.Audio => ShowClass.Setup,
        _ => ShowClass.Mapping,
    };

    /// <summary>Classes whose lines move the air, a transport, a panic or a projector's power: a word on such a line may raise
    /// its risk and never lower it.</summary>
    public static bool RiskOnlyRises(ShowClass show) =>
        show is ShowClass.Air or ShowClass.Transport or ShowClass.Panic or ShowClass.DevicePower;
}
