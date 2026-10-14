using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Timing;

/// <summary>What the display shows of an output, in one word: a GL window's swap ("gl"), composed by a compositor, an
/// independent flip, or no display at all ("offscreen").</summary>
public enum PresentMode
{
    Unknown,
    Gl,
    Composed,
    IndependentFlip,
    Offscreen,
}

/// <summary>How the mode is known: not at all, from a hint (the platform's own report), or proven (a measured run).</summary>
public enum ModeEvidence
{
    None,
    Hint,
    Proven,
}

/// <summary>One output's presentation as the display sees it, numbers only (made on the render thread without allocating):
/// the path and mode, the refresh measured against the nominal, whether the fit has settled, the counters, how old the last
/// display-side sample is, and the late share of the last 10 s that lights the output.</summary>
public readonly record struct PresentSnapshot(
    PresentPath Path,
    PresentMode Mode,
    ModeEvidence Evidence,
    bool Measured,
    double RefreshHz,
    double NominalHz,
    double Ppm,
    bool Settled,
    long Presented,
    long MissedVblanks,
    long Missed60s,
    long Glitches,
    long Repeats,
    long Held,
    long Late10s,
    long Refreshes10s,
    int DelayFrames,
    double LatencyResidualMs,
    double StatsAgeMs,
    long LastVblankNs)
{
    /// <summary>The share of the last 10 s of refreshes that showed an old picture because the new one was late, above which the
    /// output is amber (0.5 %: three refreshes in ten seconds at 60 Hz).</summary>
    public const double LateBudget = 0.005;

    /// <summary>The late share of the last 10 s (0 with no refreshes counted).</summary>
    public double LateShare => Refreshes10s > 0 ? Late10s / (double)Refreshes10s : 0;

    /// <summary>Late presents of the last 10 s beyond the 0.5 % budget (0 within it): what makes the output amber.</summary>
    public long LateOverBudget => LateShare > LateBudget ? Late10s : 0;

    /// <summary>Grey before anything was presented; amber over the late budget; green otherwise. (Whether the output is open at
    /// all, or its lock fell out, is lit by its owner on top of this.)</summary>
    public Light Light => Presented == 0 ? Light.Grey : LateOverBudget > 0 ? Light.Amber : Light.Green;

    public string PathWord => Path switch
    {
        PresentPath.GlWindow => "gl-window",
        PresentPath.DxgiFlip => "dxgi-flip",
        _ => "offscreen",
    };

    public string ModeWord => Mode switch
    {
        PresentMode.Gl => "gl",
        PresentMode.Composed => "composed",
        PresentMode.IndependentFlip => "iflip",
        PresentMode.Offscreen => "offscreen",
        _ => "unknown",
    };

    public string EvidenceWord => Evidence switch
    {
        ModeEvidence.Hint => "hint",
        ModeEvidence.Proven => "proven",
        _ => "none",
    };

    /// <summary>"59.999823 Hz · 0 missed", "measuring 59.94 Hz (12 of 30 samples)", "60 Hz target · offscreen · 0 missed",
    /// "60 Hz nominal · presents without waiting · not measured".</summary>
    public string Words()
    {
        var c = CultureInfo.InvariantCulture;
        if (Presented == 0)
            return string.Create(c, $"{NominalHz:0.###} Hz · nothing presented yet");
        var missed = string.Create(c, $"{Missed60s} missed in 60 s");
        var over = LateOverBudget > 0 ? string.Create(c, $" · {LateShare * 100:0.0} % late in 10 s") : "";
        if (Path == PresentPath.Offscreen)
            return string.Create(c, $"{NominalHz:0.###} Hz target · offscreen · {missed}{over}");
        if (!Measured)
            return string.Create(c, $"{NominalHz:0.###} Hz nominal · presents without waiting · not measured");
        if (!Settled)
            return string.Create(c, $"measuring {RefreshHz:0.###} Hz · {missed}{over}");
        return string.Create(c, $"{RefreshHz:0.000000} Hz · {missed}{over}");
    }
}

/// <summary>One output's vblank truth and ledger, fed by the renderer once a frame on the render thread:
/// <see cref="BeginFrame"/> when the frame's work starts (the refresh it is meant for), <see cref="OnPresented"/> when its present
/// returned. A measured clock is an output whose present waits for the vblank (the pacing window): its swap returns are fitted,
/// and a present is late when it returned after the refresh it was meant for. A nominal clock (offscreen, or a window that does
/// not wait) counts its presents against its own nominal refresh: a gap of n periods is n − 1 missed. Allocation-free.</summary>
public sealed class PresentClock
{
    private ulong _nextId;
    private ulong _expected;
    private bool _began;

    public PresentClock(double nominalHz, bool measured, PresentPath path)
    {
        Path = path;
        Measured = measured;
        Vblank = new VblankClock(nominalHz) { FixedPeriod = !measured };
        Ledger = new PresentLedger();
    }

    public VblankClock Vblank { get; private set; }

    public PresentLedger Ledger { get; }

    public PresentPath Path { get; }

    /// <summary>Fed by presents that wait for the vblank (fitted), rather than counted against the nominal refresh.</summary>
    public bool Measured { get; private set; }

    public double NominalHz => Vblank.NominalHz;

    /// <summary>Whole frames this output's picture is held back to line up with slower displays (0 this round).</summary>
    public int DelayFrames { get; set; }

    /// <summary>Switches between measured and nominal (the pacing output changed): the fit starts again; the ledger keeps counting.</summary>
    public void SetMeasured(bool measured)
    {
        if (measured == Measured)
            return;
        Measured = measured;
        Vblank = new VblankClock(Vblank.NominalHz) { FixedPeriod = !measured };
        _began = false;
    }

    /// <summary>The frame's work starts at <paramref name="nowNs"/>: on a measured clock, it is meant for the next refresh — or, when it
    /// starts in the last quarter period before a vblank (a swap's return jitters around the fitted instant), for the one after it.</summary>
    public void BeginFrame(long nowNs)
    {
        if (!Measured)
            return;
        _expected = Vblank.HasPhase ? Vblank.IndexAt(nowNs + (long)(Vblank.PeriodNs / 4)) + 1 : 0;
        _began = Vblank.HasPhase;
    }

    /// <summary>The present returned: a queued picture is recorded and shown at the refresh its return names; a held one is held.</summary>
    public void OnPresented(in PresentResult result)
    {
        if (!result.Queued)
        {
            Ledger.OnHeld();
            return;
        }
        var id = ++_nextId;
        var t = result.ReturnedNs;
        if (Measured)
        {
            var had = Vblank.HasPhase;
            if (Vblank.AddSwapReturn(t) == 0)
            {
                // Two returns in one vblank: the earlier picture was replaced before it showed.
                Ledger.OnPresent(id, Vblank.LastIndex);
                return;
            }
            var shown = Vblank.LastIndex;
            Ledger.OnPresent(id, _began && had ? _expected : shown);
            Ledger.OnShown(id, shown, t);
            _began = false;
            return;
        }
        var expected = Vblank.HasPhase ? Vblank.LastIndex + 1 : 0;
        Ledger.OnPresent(id, expected);
        // A second picture within one refresh replaces the first before it shows: recorded, never shown.
        if (Vblank.AddSwapReturn(t) > 0)
            Ledger.OnShown(id, Vblank.LastIndex, t);
    }

    /// <summary>Zeroes the counters and starts the fit again (PRESENT RESET); answers the totals that were there.</summary>
    public PresentSnapshot Reset(long nowNs)
    {
        var before = Snapshot(nowNs);
        Ledger.Reset();
        Vblank = new VblankClock(Vblank.NominalHz) { FixedPeriod = !Measured };
        _began = false;
        _nextId = 0;
        return before;
    }

    /// <summary>The numbers now, for STATE (no allocation).</summary>
    public PresentSnapshot Snapshot(long nowNs)
    {
        var mode = Path switch
        {
            PresentPath.GlWindow => PresentMode.Gl,
            PresentPath.Offscreen => PresentMode.Offscreen,
            _ => PresentMode.Unknown,
        };
        var age = Ledger.LastShownNs < 0 ? 0 : (nowNs - Ledger.LastShownNs) / 1e6;
        return new PresentSnapshot(Path, mode, ModeEvidence.None, Measured, Measured ? Vblank.RefreshHz : Vblank.NominalHz, Vblank.NominalHz,
            Measured ? Vblank.Ppm : 0, Measured && Vblank.Settled, Ledger.Presented, Ledger.MissedVblanks, Ledger.Missed60s(nowNs),
            Ledger.Glitches, Ledger.Repeats, Ledger.Held, Ledger.MissedIn(nowNs, 10), Ledger.RefreshesIn(nowNs, 10), DelayFrames, 0,
            Math.Max(0, age), Vblank.LastVblankNs);
    }
}
