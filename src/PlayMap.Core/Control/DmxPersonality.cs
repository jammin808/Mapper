using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PlayMap.Core.Model;

namespace PlayMap.Core.Control;

/// <summary>The built-in DMX personality ("playmap"): a channel block a lighting desk patches like a fixture. Per surface, in the
/// show's order, four channels — intensity (its opacity), source index (the item of the playlist it shows; 0 = no change), speed
/// (that playlist's rate, 50 % = 1×) and shown (above 56 % on, below 44 % off) — then per output one channel, its master
/// (brightness 0–1). A surface that shows no playlist keeps its index and speed channels, unbound, so the sheet never shifts.</summary>
public static class DmxPersonality
{
    public const string Id = "playmap";

    public const int ChannelsPerSurface = 4;

    /// <summary>The bindings and the lighting designer's channel sheet for a show, from a start address in a universe.</summary>
    /// <param name="newId">A fresh binding id per call.</param>
    public static (ImmutableArray<ControlBinding> Bindings, string Sheet, int Channels) Build(Project project, InputSource source, int universe,
        int start, string surface, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(newId);
        var word = source == InputSource.Sacn ? "SACN" : "ARTNET";
        var prefix = surface.Length > 0 ? $"{word} {surface} " : $"{word} ";
        var u = universe.ToString(CultureInfo.InvariantCulture);
        var bindings = ImmutableArray.CreateBuilder<ControlBinding>();
        var sheet = new StringBuilder();
        var ch = start;

        void Add(string what, string? target, ControlShape shape)
        {
            sheet.Append("ch").Append(ch.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(what);
            if (target is null)
                sheet.Append(" (unused)");
            sheet.Append('\n');
            if (target is not null)
                bindings.Add(new ControlBinding(newId(), what, prefix + u + " " + ch.ToString(CultureInfo.InvariantCulture), target, shape, ControlFeedback.Default, true));
            ch++;
        }

        foreach (var s in project.Stage.Surfaces)
        {
            var list = s.Content.Source.Kind == SourceKind.Playlist ? project.FindPlaylist(s.Content.Source.Id) : null;
            Add($"{s.Name} intensity", $"SET {s.Id} opacity *", ControlShape.Default with { Behaviour = ControlBehaviour.Absolute, Takeover = TakeoverMode.None });
            Add($"{s.Name} source index", list is null ? null : $"PLAYLIST GO {list.Id} #*",
                ControlShape.Default with { Behaviour = ControlBehaviour.Absolute, OutMin = 0, OutMax = list?.Items.Length ?? 1, Takeover = TakeoverMode.None });
            Add($"{s.Name} speed", list is null ? null : $"VIDEO RATE * ON {list.Id}",
                ControlShape.Default with { Behaviour = ControlBehaviour.Absolute, OutMin = 0.25, OutMax = 4, Takeover = TakeoverMode.None });
            Add($"{s.Name} shown", $"SURFACE SHOW {s.Id} ON", ControlShape.Default with { Behaviour = ControlBehaviour.Momentary, DebounceMs = 0 });
        }
        foreach (var o in project.Outputs)
        {
            Add($"{o.Name} master", $"SET {o.Id} color.brightness *",
                ControlShape.Default with { Behaviour = ControlBehaviour.Absolute, OutMin = 0, OutMax = 1, Takeover = TakeoverMode.None });
        }
        return (bindings.ToImmutable(), sheet.ToString(), ch - start);
    }
}
