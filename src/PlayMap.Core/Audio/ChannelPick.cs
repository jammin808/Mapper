using System.Globalization;

namespace PlayMap.Core.Audio;

/// <summary>How a listener takes channels from a device: every channel as it comes ("all"), one channel ("1"), two summed into one
/// ("1+2", the average so full scale stays full scale), a stereo pair ("3-4"), or mid ("M", (L+R)/2) and side ("S", (L−R)/2) of
/// the first pair. Channels count from 1.</summary>
public readonly record struct ChannelPick(ChannelPickKind Kind, int First, int Second)
{
    public static readonly ChannelPick All = new(ChannelPickKind.All, 0, 0);

    /// <summary>The channels this pick gives out.</summary>
    public int OutChannels(int deviceChannels) => Kind switch
    {
        ChannelPickKind.All => deviceChannels,
        ChannelPickKind.Pair => 2,
        _ => 1,
    };

    /// <summary>The highest device channel the pick reads (0 for all).</summary>
    public int Needs => Kind switch
    {
        ChannelPickKind.All => 0,
        ChannelPickKind.One => First,
        ChannelPickKind.Mid or ChannelPickKind.Side => 2,
        _ => Math.Max(First, Second),
    };

    /// <summary>Reads "all", "1", "1+2", "3-4", "M" or "S" (case-insensitive); the reason when it is not one.</summary>
    public static bool TryParse(string? text, out ChannelPick pick, out string why)
    {
        pick = All;
        why = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Equals("M", StringComparison.OrdinalIgnoreCase))
        {
            pick = new ChannelPick(ChannelPickKind.Mid, 1, 2);
            return true;
        }
        if (t.Equals("S", StringComparison.OrdinalIgnoreCase))
        {
            pick = new ChannelPick(ChannelPickKind.Side, 1, 2);
            return true;
        }
        foreach (var (sep, kind) in new[] { ('+', ChannelPickKind.Sum), ('-', ChannelPickKind.Pair) })
        {
            var at = t.IndexOf(sep, StringComparison.Ordinal);
            if (at <= 0)
                continue;
            if (Channel(t[..at], out var a) && Channel(t[(at + 1)..], out var b) && a != b)
            {
                pick = new ChannelPick(kind, a, b);
                return true;
            }
            why = $"'{t}' is not a channel pick: 1, 1+2 (summed), 3-4 (a pair), M or S, channels 1 to 64";
            return false;
        }
        if (Channel(t, out var one))
        {
            pick = new ChannelPick(ChannelPickKind.One, one, 0);
            return true;
        }
        why = $"'{t}' is not a channel pick: 1, 1+2 (summed), 3-4 (a pair), M or S, channels 1 to 64";
        return false;
    }

    private static bool Channel(string s, out int channel) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out channel) && channel is >= 1 and <= 64;

    /// <summary>Picks <paramref name="frames"/> frames of <paramref name="deviceChannels"/> interleaved channels into
    /// <paramref name="into"/> (<see cref="OutChannels"/> per frame). A channel the device lacks reads silence.</summary>
    public void Apply(ReadOnlySpan<float> interleaved, int deviceChannels, Span<float> into)
    {
        var frames = interleaved.Length / Math.Max(1, deviceChannels);
        for (var f = 0; f < frames; f++)
        {
            var row = interleaved.Slice(f * deviceChannels, deviceChannels);
            switch (Kind)
            {
                case ChannelPickKind.All:
                    row.CopyTo(into.Slice(f * deviceChannels, deviceChannels));
                    break;
                case ChannelPickKind.One:
                    into[f] = At(row, First);
                    break;
                case ChannelPickKind.Sum:
                case ChannelPickKind.Mid:
                    into[f] = 0.5f * (At(row, First) + At(row, Second));
                    break;
                case ChannelPickKind.Side:
                    into[f] = 0.5f * (At(row, First) - At(row, Second));
                    break;
                default:
                    into[2 * f] = At(row, First);
                    into[2 * f + 1] = At(row, Second);
                    break;
            }
        }
    }

    private static float At(ReadOnlySpan<float> row, int channel) => channel >= 1 && channel <= row.Length ? row[channel - 1] : 0f;

    public override string ToString() => Kind switch
    {
        ChannelPickKind.All => "all",
        ChannelPickKind.One => First.ToString(CultureInfo.InvariantCulture),
        ChannelPickKind.Sum => string.Create(CultureInfo.InvariantCulture, $"{First}+{Second}"),
        ChannelPickKind.Pair => string.Create(CultureInfo.InvariantCulture, $"{First}-{Second}"),
        ChannelPickKind.Mid => "M",
        _ => "S",
    };
}

public enum ChannelPickKind { All, One, Sum, Pair, Mid, Side }
