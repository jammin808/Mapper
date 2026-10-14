namespace PlayMap.Core.Audio;

/// <summary>A backend that can say why it lists what it lists ("pulse lists no capture device", "WASAPI is Windows only"): AUDIO
/// INPUTS shows the words beside an empty list instead of an empty list alone.</summary>
public interface IAudioInputsWords
{
    /// <summary>The backend's name in STATE and AUDIO INPUTS ("ffmpeg", "wasapi", "test").</summary>
    string Backend { get; }

    /// <summary>Why the list is what it is, or "" when there is nothing to say.</summary>
    string Words { get; }
}
