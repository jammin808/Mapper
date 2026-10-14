using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Modulation;
using PlayMap.Core.Preflight;

namespace PlayMap.Engine.Audio;

/// <summary>Preflight's audio rows: when bindings read the sound, a listening input is present and not silent. No input while
/// bindings listen is red with the line that gives the room something to hear (the machine's default input); an input that cannot
/// open is red with its words; one silent for more than 10 s is amber. A show whose bindings read no sound answers one green row
/// (the beat clock and LFOs need no input).</summary>
internal sealed class AudioPreflight : IPreflightCheck
{
    public string Section => "audio";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var plan = engine.LanePlan();
        var listeners = plan.Items.Where(i => i.Source is { IsAudio: true } && i.Problem.Length == 0).ToList();
        var rows = new List<PreflightRow>();
        if (listeners.Count == 0)
        {
            rows.Add(new PreflightRow("audio", node, Section, "Audio input", Light.Green, "not needed",
                plan.Items.IsEmpty ? "no modulation in the show" : "no binding reads the sound", null));
            return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
        }
        var rt = engine.AudioRt;
        var state = rt.State;
        var facts = rt.Facts;
        var count = $"{listeners.Count} binding{(listeners.Count == 1 ? "" : "s")} read the sound";
        var row = state.State switch
        {
            "listening" when facts.Silent && facts.SilentForS > 10 => new PreflightRow("audio", node, Section, "Audio input", Light.Amber,
                "silent", string.Create(CultureInfo.InvariantCulture, $"{state.Name} has been silent for {facts.SilentForS:0} s; {count}"), null),
            "listening" => new PreflightRow("audio", node, Section, "Audio input", Light.Green, "listening", $"{state.Name}; {count}", null),
            "opening" => new PreflightRow("audio", node, Section, "Audio input", Light.Amber, "opening", $"{state.Input} is opening; {count}", null),
            "retrying" => new PreflightRow("audio", node, Section, "Audio input", Light.Red, "missing",
                $"{state.Input}: {state.Words}; {count}", "AUDIO LISTEN DEFAULT"),
            _ => new PreflightRow("audio", node, Section, "Audio input", Light.Red, "off",
                $"the show listens to nothing and {count}", "AUDIO LISTEN DEFAULT"),
        };
        rows.Add(row);
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}
