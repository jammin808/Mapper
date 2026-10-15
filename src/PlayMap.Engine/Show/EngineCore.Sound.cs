using System.Text.Json;
using PlayMap.Engine.Show;
using PlayMap.Media.Audio;

namespace PlayMap.Engine;

/// <summary>Sound on the air (wp2-blind): one <see cref="AirAudio"/> the node's sound part (or a test) starts on an output; the
/// lane's tick tells it what plays, and STATE.show.sound says what it plays and at what gain.</summary>
public sealed partial class EngineCore
{
    private AirAudio? _airAudio;

    /// <summary>The air's sound now, or null when the node plays none.</summary>
    public AirAudio? AirSound => Volatile.Read(ref _airAudio);

    /// <summary>Plays the on-air item's sound through <paramref name="output"/> (the caller disposes what this answers; that lets
    /// the output go). A second start replaces the first, which stops following the show.</summary>
    public AirAudio StartAirAudio(AudioOut output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var sound = new AirAudio(this, output);
        Volatile.Write(ref _airAudio, sound);
        FactsChanged();
        return sound;
    }

    internal void DetachAirAudio(AirAudio sound)
    {
        if (Interlocked.CompareExchange(ref _airAudio, null, sound) == sound)
            FactsChanged();
    }

    private void TickSound(long leaderNs) => Volatile.Read(ref _airAudio)?.Follow(_snapshot, leaderNs);

    /// <summary>STATE.show.sound: null without sound; else the output, the item playing, the air's gain now and any problem.</summary>
    private void WriteSound(Utf8JsonWriter w)
    {
        if (Volatile.Read(ref _airAudio) is not { } sound)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteString("output", sound.Output.Mode.ToString().ToLowerInvariant());
        w.WriteString("status", sound.Output.Status);
        if (sound.ItemId is { } item)
            w.WriteString("item", item);
        else
            w.WriteNull("item");
        w.WriteNumber("gain", Math.Round(AirAudioGain(LeaderNowNs), 3));
        w.WriteString("problem", sound.Problem);
        w.WriteEndObject();
    }
}
