using System.Collections.Immutable;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.Panasonic;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Native;

/// <summary>One of a projector's own processing settings as read: its name, the protocol's word, the value read, the value that turns it
/// off, whether it is on, whether the word is from the vendor's document for every model (else model-dependent), and the caveat.</summary>
public sealed record NativeSetting(string Name, string Word, string Value, string Neutral, bool On, bool Verified, string Caveat);

/// <summary>What a read of a projector's own processing found: the settings read, the words it did not answer (unsupported on this
/// model), how far the read got, and the words.</summary>
public sealed record NativeReading(ImmutableArray<NativeSetting> Settings, ImmutableArray<string> Unsupported, CommandResult Result, string Words)
{
    /// <summary>The projector's own geometry correction is on.</summary>
    public bool GeometryOn => Settings.Any(s => s.Name == "geometry" && s.On);

    /// <summary>The projector's own edge blend is on.</summary>
    public bool BlendOn => Settings.Any(s => s.Name == "edge blend" && s.On);

    /// <summary>The read reached the projector and it answered.</summary>
    public bool Read => Result.Stage is CommandStage.Accepted or CommandStage.Observed;
}

/// <summary>
/// A projector's own processing read live, for the double-correction guard (autoalign M4, the read half): on Panasonic, geometry
/// (<c>QVX:GMMI0</c>, off at <c>+00000</c>) and edge blending (<c>QVX:EDBI0</c>, off at <c>+00000</c>), the words of the PT-DZ21K2-series
/// table the driver already sends; a word a model answers ERR1 or ERR2 to is listed unsupported and the rest go on. Other protocols
/// answer with words saying where to look instead. Read only: nothing is ever written.
/// </summary>
public static class NativeProcessing
{
    private static readonly (string Name, string Word, string Neutral)[] Panasonic =
    [
        ("geometry", "GMMI0", "+00000"),
        ("edge blend", "EDBI0", "+00000"),
    ];

    public static async Task<NativeReading> ReadAsync(IProjectorDriver driver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (driver.Protocol != ProjectorProtocol.PanasonicNtControl)
        {
            return new NativeReading([], [], CommandResult.Refused("native read", "this protocol publishes no read of the projector's own processing"),
                $"a {driver.Protocol} projector's own geometry and blend are not read over its protocol this round — check its menu (warp, keystone, edge blend off)");
        }
        var settings = ImmutableArray.CreateBuilder<NativeSetting>();
        var unsupported = ImmutableArray.CreateBuilder<string>();
        CommandResult last = CommandResult.Refused("native read", "nothing was read");
        foreach (var (name, word, neutral) in Panasonic)
        {
            ct.ThrowIfCancellationRequested();
            var r = await driver.RawAsync("QVX:" + word, ct).ConfigureAwait(false);
            if (r.Stage is CommandStage.Unreached or CommandStage.Refused)
                return new NativeReading(settings.ToImmutable(), unsupported.ToImmutable(), r, $"{name} could not be read: {r.Words}");
            if (r.Stage == CommandStage.Rejected)
            {
                unsupported.Add(word);
                last = r;
                continue;
            }
            var body = r.Reply.StartsWith("00", StringComparison.Ordinal) ? r.Reply[2..] : r.Reply;
            if (NtControlProtocol.SubValue(body, word) is not { } value)
            {
                unsupported.Add(word);
                continue;
            }
            settings.Add(new NativeSetting(name, word, value, neutral, value != neutral, false,
                "PT-DZ21K2-series table; other models may differ"));
            last = r;
        }
        var read = settings.ToImmutable();
        var on = read.Where(s => s.On).Select(s => s.Name).ToList();
        var words = read.Length == 0 ? "the projector answered none of its own processing words"
            : on.Count == 0 ? "its own geometry and blend are off"
            : $"its own {string.Join(" and ", on)} {(on.Count == 1 ? "is" : "are")} on";
        if (unsupported.Count > 0)
            words += $" ({string.Join(", ", unsupported)} unsupported on this model)";
        return new NativeReading(read, unsupported.ToImmutable(), last, words);
    }

    /// <summary>The line that turns a setting off, in the protocol's own grammar (for DEVICE RAW).</summary>
    public static string NeutralLine(NativeSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return $"VXX:{setting.Word}={setting.Neutral}";
    }
}
