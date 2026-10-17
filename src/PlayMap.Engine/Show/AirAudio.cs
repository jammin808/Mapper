using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Engine.Playback;
using PlayMap.Media;
using PlayMap.Media.Audio;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Engine.Show;

/// <summary>Sound on the air: the on-air playlist's item (the focused playlist, the one the clip verbs drive) plays through one
/// <see cref="AudioOut"/> slaved to the item's own anchor, so it follows GO, pause, seek, a rate and a panic's transport outcome as
/// the picture does. Its gain is the air's sound gain (master × panic, <see cref="EngineCore.AirAudioGain"/>) × the item's volume,
/// computed at each service step at the leader instant of the one instant that step reads (the output runs on the node's own
/// clock, which the network clock maps to the leader's), so PANIC and MASTER fade sound on the same show clock as picture. The lane decides what plays; a decoder opens on a worker and is handed over once open; the output's thread only
/// reads. Disposing it stops the sound and lets go of the output.</summary>
public sealed class AirAudio : IAsyncDisposable
{
    private readonly EngineCore _engine;
    private readonly Lock _gate = new();
    private (string PlaylistId, string ItemId, string MediaId)? _wanted;
    private (string PlaylistId, string ItemId, string MediaId)? _playing;
    private Task? _opening;
    private bool _openDone;
    private FfmpegAudioDecoder? _opened;
    private string _openProblem = "";
    private (string PlaylistId, string ItemId, string MediaId)? _openingFor;
    private FfmpegAudioDecoder? _decoder;
    private (string PlaylistId, string ItemId, string MediaId)? _failed;
    private string _problem = "";
    private bool _disposed;

    internal AirAudio(EngineCore engine, AudioOut output)
    {
        _engine = engine;
        Output = output;
    }

    /// <summary>The output the air's sound plays through.</summary>
    public AudioOut Output { get; }

    /// <summary>The item playing now, or null.</summary>
    public string? ItemId
    {
        get
        {
            lock (_gate)
                return _playing?.ItemId;
        }
    }

    /// <summary>Why the wanted item has no sound ("" when it plays or nothing is wanted).</summary>
    public string Problem
    {
        get
        {
            lock (_gate)
                return _problem;
        }
    }

    /// <summary>On the lane's tick: what should sound now, and the hand-over of a decoder that finished opening.</summary>
    internal void Follow(EngineSnapshot snap, long leaderNs)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _wanted = Wanted(snap, leaderNs);
            if (_failed is not null && _wanted != _failed)
            {
                _failed = null;
                _problem = "";
            }
            if (_opening is not null && _openDone)
            {
                var opened = _opened;
                var forItem = _openingFor;
                var why = _openProblem;
                _opened = null;
                _openDone = false;
                _opening = null;
                _openingFor = null;
                _openProblem = "";
                if (opened is not null && forItem == _wanted)
                {
                    Hand(opened, forItem!.Value);
                    return;
                }
                // A file that cannot sound: said here, on the tick that silences the item before it, so the item and its problem
                // move together.
                if (opened is null)
                {
                    _failed = forItem;
                    if (forItem == _wanted)
                        _problem = why;
                }
                opened?.Dispose();
            }
            // Nothing wanted, nothing to say: a failure was the item's, and the item has left the air.
            if (_wanted is null)
                _problem = "";
            if (_wanted == _playing)
                return;
            if (_wanted is null || _wanted == _failed)
            {
                Silence();
                return;
            }
            if (_opening is null && snap.Project.Project.FindMedia(_wanted.Value.MediaId) is { } media)
            {
                _openingFor = _wanted;
                _opening = StartOpen(PathOf(media));
            }
        }
    }

    /// <summary>Opens <paramref name="path"/> on a worker. Its own method: the worker's closure (it captures the path) is made only
    /// when a decoder opens. Written in <see cref="Follow"/>, an optimised build would allocate that closure at the method's entry, on
    /// every tick of the show lane while sound is attached.</summary>
    private Task StartOpen(string path) => Task.Run(() => Open(path));

    /// <summary>The bytes the item names on this machine, as the picture finds them: the file fetched by its hash, else its location
    /// (a relative one against the project's folder).</summary>
    private string PathOf(MediaItem media)
    {
        if (ContentHasher.IsValid(media.ContentHash) && _engine.Services.Media?.PathForHash(media.ContentHash) is { } stored)
            return stored;
        var folder = _engine.ProjectPath is { } project ? Path.GetDirectoryName(Path.GetFullPath(project)) : _engine.Services.ProjectFolder;
        var path = MediaLibrary.ResolvePath(media, folder);
        // The source first; when it does not resolve here, the version the picture plays (wp2-optimise), when that one has sound.
        if (File.Exists(path) || _engine.Services.Media is not { } backend)
            return path;
        var formats = (backend as Host.MediaBackend)?.Formats ?? Core.Media.BlockFormats.Bc1 | Core.Media.BlockFormats.Bc3 | Core.Media.BlockFormats.Bc4;
        var played = Core.Media.Optimise.VersionChoice.Played(media, formats, h => backend.PathForHash(h) is not null);
        return !ReferenceEquals(played, media) && played.Info.AudioChannels > 0 && backend.PathForHash(played.ContentHash) is { } version ? version : path;
    }

    /// <summary>The focused playlist's item on the air at this instant — the incoming one from the instant it first shows, as the
    /// picture's fade brings it in — when its source is a media file with sound.</summary>
    private static (string, string, string)? Wanted(EngineSnapshot snap, long leaderNs)
    {
        if (snap.Playback.Focused is not { IsIdle: false } lane)
            return null;
        var (source, itemId) = lane.NextItemId is not null && leaderNs >= lane.NextShowsAtNs
            ? (lane.NextSource, lane.NextItemId)
            : (lane.Source, lane.ItemId);
        if (itemId is null || source.Kind != SourceKind.Media || snap.Project.Project.FindMedia(source.Id) is not { IsFile: true } media
            || media.Info.AudioChannels <= 0)
            return null;
        return (lane.PlaylistId, itemId, media.Id);
    }

    /// <summary>On a worker: opens the decoder and leaves it for the lane's next tick (or lets it go when the sound was disposed).</summary>
    private void Open(string path)
    {
        FfmpegAudioDecoder? decoder = null;
        var problem = "";
        try
        {
            decoder = new FfmpegAudioDecoder(path);
        }
        // Whatever stops a decoder from opening is this item's problem, said in STATE; the worker always reports back, so the sound
        // never stops following the show over one file.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = $"{Path.GetFileName(path)} has no sound this node can play: {ex.Message}";
        }
        lock (_gate)
        {
            if (_disposed)
            {
                decoder?.Dispose();
                return;
            }
            _opened = decoder;
            _openProblem = problem;
            _openDone = true;
        }
    }

    /// <summary>The opened decoder plays, slaved to its item's anchor; the one before is let go once the output holds the new one.</summary>
    private void Hand(FfmpegAudioDecoder decoder, (string PlaylistId, string ItemId, string MediaId) item)
    {
        // The first service step plays at the air's gain now, never at what the output last had (a panic or MASTER 0 while idle).
        Output.Volume = GainAt(item.PlaylistId, _engine.LeaderNowNs);
        Output.Play(decoder, new AirTimeline(this, item.PlaylistId, item.ItemId));
        _decoder?.Dispose();
        _decoder = decoder;
        _playing = item;
        _problem = "";
    }

    private void Silence()
    {
        Output.Stop();
        _decoder?.Dispose();
        _decoder = null;
        _playing = null;
    }

    /// <summary>The item's anchor on the playlist as published now (held still once it is no longer the one on the air).</summary>
    private TimelineAnchor AnchorOf(string playlistId, string itemId)
    {
        if (!_engine.Snapshot.Playback.Playlists.TryGetValue(playlistId, out var lane))
            return TimelineAnchor.Zero;
        var clip = lane.ItemId == itemId ? lane.Clip : lane.NextItemId == itemId ? lane.NextClip : null;
        return clip?.Anchor ?? TimelineAnchor.Zero;
    }

    /// <summary>The air's gain × the playlist's volume at a leader instant.</summary>
    private float GainAt(string playlistId, long leaderNs)
    {
        var volume = _engine.Snapshot.Playback.Playlists.TryGetValue(playlistId, out var lane) ? lane.Volume : 1;
        return (float)(_engine.AirAudioGain(leaderNs) * Math.Clamp(volume, 0, 4));
    }

    /// <summary>The item's timeline as the output's service step reads it, at the leader instant of the step's one instant: the
    /// anchor's position there, and the air's gain × the playlist's volume there, which the output applies from its next step.</summary>
    private sealed class AirTimeline(AirAudio owner, string playlistId, string itemId) : IAudioTimeline
    {
        public TimelinePosition At(long localNs)
        {
            var leaderNs = owner._engine.Services.Clock.ToLeader(localNs);
            owner.Output.Volume = owner.GainAt(playlistId, leaderNs);
            return AnchorTimeline.Position(owner.AnchorOf(playlistId, itemId), leaderNs);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? opening;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            opening = _opening;
            _opening = null;
        }
        _engine.DetachAirAudio(this);
        Output.Dispose();
        // A decoder still opening lets itself go when it sees the sound disposed; one opened but not handed over goes here.
        if (opening is not null)
            await opening.ConfigureAwait(false);
        lock (_gate)
        {
            _decoder?.Dispose();
            _decoder = null;
            _opened?.Dispose();
            _opened = null;
            _playing = null;
        }
    }
}
