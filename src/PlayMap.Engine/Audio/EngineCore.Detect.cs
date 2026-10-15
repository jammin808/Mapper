using PlayMap.Core.Audio;

namespace PlayMap.Engine;

/// <summary>TEMPO SOURCE DETECT: the lane's tick hands every new frame's onset strength to the tempo tracker; every 0.5 s of sound it
/// estimates, and once two estimates running are sure (confidence 0.3 or more) the clock is locked to it — a tempo off by more than
/// 0.2 BPM or a beat off by more than 10 ms is corrected one lead ahead like any change: slewed (at most 2 % for whole bars) when
/// the beat is within a quarter of the detected one, jumped when it is further. Silence holds the clock (it keeps counting); 32 bars
/// of it is "lost". The show's tempo is not written: detection is this node's.</summary>
public sealed partial class EngineCore
{
    private const double SureEnough = 0.3;
    private const long BeatToleranceNs = 10_000_000;
    private const double TempoTolerance = 0.2;

    private TempoTracker _tracker = new();
    private readonly AnalysisFrame _detectFrame = new();
    private long _trackedIndex = -1;
    private long _trackedCount;
    private volatile string _tempoLock = "manual";
    private double _tempoConfidence = -1;
    private int _sureRuns;
    private long _silentSinceNs = long.MinValue;

    /// <summary>The detection's state for STATE and TEMPO STATUS: manual, searching, locked, holding or lost.</summary>
    internal string TempoLockNow => _tempoLock;

    internal double TempoConfidenceNow => Volatile.Read(ref _tempoConfidence);

    /// <summary>A new source starts the detection afresh (searching), or ends it (manual), at once.</summary>
    private void DetectionSource(string source)
    {
        _tracker.Reset();
        _trackedIndex = -1;
        _sureRuns = 0;
        _silentSinceNs = long.MinValue;
        _tempoLock = source == "detect" ? "searching" : "manual";
        Volatile.Write(ref _tempoConfidence, -1);
    }

    /// <summary>Feeds the tracker and keeps the clock locked to it (on the lane); true when what STATE shows moved.</summary>
    private bool DetectTempo(long leaderNs)
    {
        var before = (_tempoLock, Math.Round(_tempoConfidence, 2));
        if (Store.Project.Tempo.Source != "detect")
        {
            // An undo or a loaded show can change the source without the verb.
            if (_tempoLock != "manual")
                DetectionSource("manual");
            return before != (_tempoLock, Math.Round(_tempoConfidence, 2));
        }
        if (_tempoLock == "manual")
            DetectionSource("detect");
        var t = Store.Project.Tempo;
        if (t.DetectMinBpm != _tracker.MinBpm || t.DetectMaxBpm != _tracker.MaxBpm)
        {
            // The show's window: a window that cannot be searched keeps the usual one.
            var min = Math.Clamp(t.DetectMinBpm, 30, 290);
            var max = Math.Clamp(t.DetectMaxBpm, min + 10, 300);
            if (min != _tracker.MinBpm || max != _tracker.MaxBpm)
            {
                _tracker = new TempoTracker(min, max);
                _trackedIndex = -1;
                _sureRuns = 0;
            }
        }
        var ring = AudioRt.Feed.Ring;
        var count = ring.Count;
        if (count < _trackedCount)
        {
            // Another input: what the tracker held was another sound.
            _tracker.Reset();
            _trackedIndex = -1;
            _sureRuns = 0;
        }
        _trackedCount = count;
        if (AudioRt.Feed.Listening)
        {
            for (var i = Math.Max(_trackedIndex + 1, count - 1500); i < count; i++)
            {
                if (!ring.TryRead(i, _detectFrame))
                    continue;
                // The strength compares a hop's window with the one before: it stands half a hop before the frame's instant.
                _tracker.Push(_detectFrame.OnsetStrength, _detectFrame.LeaderNs - AudioAnalyser.Hop * 1_000_000_000L / AudioAnalyser.SampleRate / 2);
                _trackedIndex = i;
                if (_tracker.Frames % 100 == 0)
                    Follow(_tracker.Update(), leaderNs);
            }
            Silence(leaderNs, _detectFrame.Silent);
        }
        else
        {
            Silence(leaderNs, true);
        }
        return before != (_tempoLock, Math.Round(_tempoConfidence, 2));
    }

    private void Silence(long leaderNs, bool silent)
    {
        if (!silent)
        {
            _silentSinceNs = long.MinValue;
            if (_tempoLock is "holding" or "lost" && _sureRuns >= 2)
                _tempoLock = "locked";
            return;
        }
        if (_silentSinceNs == long.MinValue)
            _silentSinceNs = leaderNs;
        if (_tempoLock is "locked" or "holding")
        {
            var clock = AudioRt.Tempo;
            var barsSilent = (leaderNs - _silentSinceNs) * clock.BpmAt(leaderNs) / 60e9 / clock.BeatsPerBar;
            _tempoLock = barsSilent > 32 ? "lost" : "holding";
        }
    }

    /// <summary>Takes an estimate: sure twice running locks; a locked clock off the estimate is corrected one lead ahead.</summary>
    private void Follow(TempoEstimate? estimate, long leaderNs)
    {
        if (estimate is not { } e)
            return;
        Volatile.Write(ref _tempoConfidence, Math.Round(e.Confidence, 3));
        if (e.Confidence < SureEnough)
        {
            _sureRuns = 0;
            if (_tempoLock == "locked")
                _tempoLock = "holding";
            return;
        }
        if (++_sureRuns < 2)
            return;
        _tempoLock = "locked";
        _tempoInUse = true;
        var clock = AudioRt.Tempo.Kept(leaderNs);
        // A correction still slewing is left to finish before the next one is judged.
        if (clock.Anchors.Any(a => a.LeaderNs > leaderNs))
            return;
        var bpm = Math.Round(e.Bpm, 2);
        var at = leaderNs + TempoTimeline.LeadNs;
        var target = Math.Round(clock.BeatAt(e.BeatLeaderNs)) + (at - e.BeatLeaderNs) * bpm / 60e9;
        var error = target - clock.BeatAt(at);
        if (Math.Abs(bpm - clock.BpmAt(at)) <= TempoTolerance && Math.Abs(error) * 60e9 / bpm <= BeatToleranceNs)
            return;
        AudioRt.Tempo = Math.Abs(error) > 0.25
            ? clock.WithBeat(at, target, "detect").WithTempo(at, bpm, "detect")
            : clock.WithTempo(at, bpm, "detect").WithNudge(at, (long)Math.Round(error * 60e9 / bpm), "detect");
    }
}
