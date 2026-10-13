using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>The show's control map: the controllers it names and the bindings from their inputs to wire lines. Which port
/// a surface is on this machine is the node's (NodeSettings.Control.SurfacePorts).</summary>
public sealed record ControlSet
{
    public static readonly ControlSet Empty = new();
    public ImmutableArray<ControlSurface> Surfaces { get; init; } = [];
    public ImmutableArray<ControlBinding> Bindings { get; init; } = [];
}

/// <summary>A controller the show's bindings name.</summary>
/// <param name="Name">The word input lines use ("APC40"): letters, digits, '-', '_', 1–32 characters, unique.</param>
/// <param name="Kind">"midi", "osc", "artnet" or "sacn".</param>
/// <param name="Match">midi: port-name glob; osc: sender address[:port]; artnet/sacn: source address or CID; "" = any.</param>
/// <param name="Profile">A controller profile id ("mackie-control"); "" = generic.</param>
/// <param name="FeedbackTo">midi: output-port glob ("" = the input's name); osc: host:port ("" = the sender's).</param>
public sealed record ControlSurface(string Id, string Name, string Kind, string Match, string Profile, string FeedbackTo,
    bool Enabled) : IEntity
{
    /// <summary>Equal-priority DMX sources: "ltp" or "htp"; a lost DMX source leaves "hold" or "release".</summary>
    public string Merge { get; init; } = "ltp";
    public string OnLoss { get; init; } = "hold";
    public bool AcceptPreview { get; init; }
}

/// <summary>A control input bound to a wire line: the line runs through the gate like any other, undoable and journalled per gesture.</summary>
/// <param name="Input">Input words: "MIDI APC40 CC 1 48", "OSC /fader/1", "ARTNET 0 101+102", "SACN 7 101".</param>
/// <param name="Target">A wire line; "*" carries the value ("SET sf… opacity *"), "#*" an item number, "*%" percent.</param>
public sealed record ControlBinding(string Id, string Name, string Input, string Target, ControlShape Shape,
    ControlFeedback Feedback, bool Enabled) : IEntity;

public enum ControlBehaviour { Auto, Trigger, Toggle, Momentary, Value, Velocity, Absolute, Relative }
public enum ControlCurve { Linear, Exponential, Logarithmic, SCurve, Steps }
public enum TakeoverMode { Auto, None, PickUp, ValueScaling }
public enum RelativeCoding { Auto, TwosComplement, BinaryOffset, SignedBit, SignedBit2, Increment }
public enum FeedbackMode { Auto, Off, Value, Lamp }

/// <summary>How an input's value becomes the target's: behaviour, ranges, curve, takeover and encoder coding.</summary>
public sealed record ControlShape
{
    public static readonly ControlShape Default = new();
    public ControlBehaviour Behaviour { get; init; } = ControlBehaviour.Auto;
    public double InMin { get; init; }
    public double InMax { get; init; } = 1;
    /// <summary>null = the target's own range.</summary>
    public double? OutMin { get; init; }
    public double? OutMax { get; init; }
    public bool Invert { get; init; }
    public ControlCurve Curve { get; init; } = ControlCurve.Linear;
    public double CurveAmount { get; init; } = 2;
    public TakeoverMode Takeover { get; init; } = TakeoverMode.Auto;
    public RelativeCoding Relative { get; init; } = RelativeCoding.Auto;
    /// <summary>0 = range ÷ 128 per tick.</summary>
    public double Step { get; init; }
    public double Acceleration { get; init; } = 0.5;
    public bool Wrap { get; init; }
    public string PressValue { get; init; } = "";
    public string ReleaseValue { get; init; } = "";
    public int HoldMs { get; init; }
    public int DebounceMs { get; init; } = 30;
}

/// <summary>What a binding sends back to its controller (a lamp, a fader position) and when.</summary>
public sealed record ControlFeedback
{
    public static readonly ControlFeedback Default = new();
    public FeedbackMode Mode { get; init; } = FeedbackMode.Auto;
    /// <summary>"" = the same control; else output words ("MIDI APC40 NOTE 1 53", "OSC 10.0.0.9:9000 /led/1").</summary>
    public string To { get; init; } = "";
    /// <summary>"" = inferred from the target; else a STATE predicate ("air.blackout").</summary>
    public string When { get; init; } = "";
    public string On { get; init; } = "";
    public string Off { get; init; } = "";
}
