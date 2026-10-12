using System.Collections.Immutable;

namespace PlayMap.Core.Model;

public enum MediaKind
{
    Video,
    Image,
    ImageSequence,
    Audio,
    /// <summary>A web page, rendered by the browser engine.</summary>
    Web,
    /// <summary>A YouTube (or other embed) video, driven through its player API; never downloaded.</summary>
    Embed,
    /// <summary>A network stream (RTSP, SRT, HLS…).</summary>
    Stream,
}

/// <summary>What the probe read from a file: enough to plan playback and to tell the operator before the
/// show what a node cannot play.</summary>
public sealed record MediaInfo(
    long DurationNs,
    int Width,
    int Height,
    string FrameRate,
    string VideoCodec,
    string PixelFormat,
    string AudioCodec,
    int AudioChannels,
    int AudioRate,
    bool HasAlpha,
    string Container)
{
    public static readonly MediaInfo Unknown = new(0, 0, 0, "", "", "", "", 0, 0, false, "");
}

/// <summary>A library item. A file is identified by its content hash ("sha256:…"), not its path: the same
/// clip on two machines in different folders is the same media, and a node that lacks it fetches it by
/// hash from a peer that has it.</summary>
public sealed record MediaItem(
    string Id,
    string Name,
    MediaKind Kind,
    /// <summary>A path on this machine (absolute, or relative to the project folder), or a URL for web,
    /// embed and stream items.</summary>
    string Location,
    /// <summary>"sha256:&lt;64 hex&gt;" for files; empty for URLs and until hashed.</summary>
    string ContentHash,
    long SizeBytes,
    MediaInfo Info,
    ImmutableArray<string> Tags) : IEntity
{
    public bool IsFile => Kind is MediaKind.Video or MediaKind.Image or MediaKind.ImageSequence or MediaKind.Audio;
}
