using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;

namespace PlayMap.Core.Media.Optimise;

/// <summary>How a chunk's bytes are stored by the second stage: per chunk, Snappy unless it saves less than the recipe's gain
/// (Auto); always Snappy; always raw.</summary>
public enum SecondStagePolicy
{
    Auto,
    Snappy,
    None,
}

/// <summary>What a derived file is made with: the target, the encoder's quality, the chunk count (0 = by width: 16 at 3840 and
/// wider, 8 at 1920 and wider, else 4), the second stage and its raw threshold, and what happens to the audio ("copy": the
/// source's stream copied with its timestamps). Its canonical text is compact JSON with sorted keys and invariant numbers, so
/// the same recipe is the same bytes on every machine, and its key names the work by source, recipe and encoder algorithm.</summary>
public sealed record Recipe(string To, EncodeQuality Quality, int Chunks, SecondStagePolicy SecondStage, double RawBelowGain, string Audio)
{
    /// <summary>The encoder algorithm's version: part of every key, never the device it ran on.</summary>
    public const string EncoderVersion = "hapenc/1";

    public static Recipe For(string to, EncodeQuality quality) => new(to, quality, 0, SecondStagePolicy.Auto, 0.10, "copy");

    /// <summary>The HAP codec the recipe writes.</summary>
    public HapCodec Codec => OptimiseTargets.TryCodec(To, out var c) ? c : throw new InvalidOperationException($"{To} is not a target this encoder makes");

    /// <summary>The chunk count for a picture this wide (the recipe's own when it names one).</summary>
    public int ChunksFor(int width) => Chunks > 0 ? Math.Clamp(Chunks, 1, HapFrame.MaxChunks) : width >= 3840 ? 16 : width >= 1920 ? 8 : 4;

    public string Canonical()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(128);
        sb.Append("{\"audio\":").Append(JsonSerializer.Serialize(Audio));
        sb.Append(",\"chunks\":").Append(Chunks.ToString(c));
        sb.Append(",\"quality\":\"").Append(QualityWord(Quality)).Append('"');
        sb.Append(",\"rawBelow\":").Append(RawBelowGain.ToString("R", c));
        sb.Append(",\"secondStage\":\"").Append(SecondStage.ToString().ToLowerInvariant()).Append('"');
        sb.Append(",\"to\":").Append(JsonSerializer.Serialize(To));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>"ok:" + the first 32 hex digits of SHA-256 over the source's hash, the canonical recipe and the encoder version.</summary>
    public string Key(string sourceHash, string encoderVersion = EncoderVersion)
    {
        var text = "playmap-optimise/1\n" + sourceHash + "\n" + Canonical() + "\n" + encoderVersion;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "ok:" + Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>A recipe from its canonical text (a job record's, a version's); null when the text is not one.</summary>
    public static Recipe? Parse(string canonical)
    {
        try
        {
            using var doc = JsonDocument.Parse(canonical);
            var r = doc.RootElement;
            if (!TryQuality(r.GetProperty("quality").GetString(), out var q)
                || !Enum.TryParse<SecondStagePolicy>(r.GetProperty("secondStage").GetString(), true, out var stage))
                return null;
            var to = r.GetProperty("to").GetString() ?? "";
            if (!OptimiseTargets.TryCodec(to, out _))
                return null;
            return new Recipe(to, q, r.GetProperty("chunks").GetInt32(), stage, r.GetProperty("rawBelow").GetDouble(), r.GetProperty("audio").GetString() ?? "copy");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static string QualityWord(EncodeQuality q) => q switch
    {
        EncodeQuality.Fast => "fast",
        EncodeQuality.Archive => "archive",
        _ => "show",
    };

    public static bool TryQuality(string? word, out EncodeQuality quality)
    {
        switch (word?.Trim().ToLowerInvariant())
        {
            case "fast":
                quality = EncodeQuality.Fast;
                return true;
            case "show":
                quality = EncodeQuality.Show;
                return true;
            case "archive":
                quality = EncodeQuality.Archive;
                return true;
            default:
                quality = EncodeQuality.Show;
                return false;
        }
    }
}
