using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Devices.Model;

/// <summary>A projector's power as it reports it. Unknown is never guessed into another state.</summary>
public enum PowerState
{
    Unknown,
    Standby,
    WarmingUp,
    On,
    CoolingDown,
}

public enum FaultSeverity
{
    Warning,
    Error,
}

/// <summary>Something the projector reports wrong, in plain words ("lamp: error"), with how bad it is.</summary>
public sealed record DeviceFault(string Words, FaultSeverity Severity);

/// <summary>A temperature the projector reports, by its own name for the sensor ("intake").</summary>
public sealed record DeviceTemperature(string Sensor, double Celsius);

/// <summary>What the projector's input receives. Width/height 0 and rate 0 = not reported; <see cref="Text"/> is
/// the projector's own words ("1080/60p", "1920x1080") so nothing the parser could not read is lost.</summary>
public sealed record SignalInfo(int Width, int Height, double RefreshHz, string Text)
{
    public static readonly SignalInfo None = new(0, 0, 0, "");

    public bool HasRaster => Width > 0 && Height > 0;
}

public enum LensAxis
{
    ShiftHorizontal,
    ShiftVertical,
    Zoom,
    Focus,
}

/// <summary>Lens motor positions in the projector's own raw units (Christie's ±2050 shift, Panasonic's ±2480):
/// null = not read. Raw units are not optics — <c>MappingWorkflows.LensFromProjector</c> says when they can be
/// turned into a throw ratio or a shift fraction and when they cannot.</summary>
public sealed record LensPositions(int? ShiftHorizontal, int? ShiftVertical, int? Zoom, int? Focus)
{
    public static readonly LensPositions Unknown = new(null, null, null, null);

    public bool Any => ShiftHorizontal.HasValue || ShiftVertical.HasValue || Zoom.HasValue || Focus.HasValue;

    public int? Get(LensAxis axis) => axis switch
    {
        LensAxis.ShiftHorizontal => ShiftHorizontal,
        LensAxis.ShiftVertical => ShiftVertical,
        LensAxis.Zoom => Zoom,
        _ => Focus,
    };

    public LensPositions With(LensAxis axis, int? value) => axis switch
    {
        LensAxis.ShiftHorizontal => this with { ShiftHorizontal = value },
        LensAxis.ShiftVertical => this with { ShiftVertical = value },
        LensAxis.Zoom => this with { Zoom = value },
        _ => this with { Focus = value },
    };

    /// <summary>"h -120 v 300 zoom ? focus 12" — a dash for an axis never read.</summary>
    public string Words()
    {
        static string W(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "?";
        return $"h {W(ShiftHorizontal)} v {W(ShiftVertical)} zoom {W(Zoom)} focus {W(Focus)}";
    }
}

/// <summary>A link's liveness, as Show Bus peers have it: live while answers keep coming, stale after a few
/// missed polls, gone after more. Never-heard links are <see cref="Unknown"/> (grey), not gone.</summary>
public enum DeviceLiveness
{
    Unknown,
    Live,
    Stale,
    Gone,
}

/// <summary>
/// What PlayMap knows about one projector, as facts read from it. Every field the protocol could not read stays
/// null/unknown — never filled from a guess. Immutable; the manager publishes a new one on each change.
/// </summary>
public sealed record DeviceState
{
    public required string LinkId { get; init; }

    public DeviceLiveness Liveness { get; init; } = DeviceLiveness.Unknown;

    /// <summary>The last exchange reached the projector (it answered). A stale or gone link is not online.</summary>
    public bool Online { get; init; }

    public PowerState Power { get; init; } = PowerState.Unknown;

    /// <summary>The input in the protocol's own words ("31", "HD1", "L1 HDMI"); null = not read.</summary>
    public string? Input { get; init; }

    /// <summary>True = shutter (or picture mute) closed; null = not read.</summary>
    public bool? ShutterClosed { get; init; }

    public bool? Frozen { get; init; }

    /// <summary>Light source hours, one per lamp/light source; empty = not read.</summary>
    public ImmutableArray<int> LightHours { get; init; } = ImmutableArray<int>.Empty;

    public ImmutableArray<DeviceTemperature> Temperatures { get; init; } = ImmutableArray<DeviceTemperature>.Empty;

    /// <summary>Faults in plain words; empty = none reported (or the protocol cannot report them — see
    /// <see cref="FaultsRead"/>).</summary>
    public ImmutableArray<DeviceFault> Faults { get; init; } = ImmutableArray<DeviceFault>.Empty;

    /// <summary>The fault list was actually read: "no faults" is a fact only when this is true.</summary>
    public bool FaultsRead { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? Serial { get; init; }

    public string? Firmware { get; init; }

    /// <summary>The projector's own name for itself (PJLink NAME).</summary>
    public string? ProjectorName { get; init; }

    public SignalInfo Signal { get; init; } = SignalInfo.None;

    public LensPositions Lens { get; init; } = LensPositions.Unknown;

    /// <summary>Monotonic time of the last answer (ns, <see cref="IMonoClock"/>); 0 = never heard.</summary>
    public long LastSeenMonoNs { get; init; }

    /// <summary>The last failure in words ("no answer from 10.0.0.7:4352"), "" when the last exchange worked.</summary>
    public string LastProblem { get; init; } = "";

    /// <summary>The Show Bus light for this projector (§6 of the context).</summary>
    public Light Light => DeviceLights.Of(this);

    /// <summary>A reading folded in: every field the reading has replaces ours; what it lacks stays.</summary>
    public DeviceState Merge(DeviceState reading) => this with
    {
        Power = reading.Power != PowerState.Unknown ? reading.Power : Power,
        Input = reading.Input ?? Input,
        ShutterClosed = reading.ShutterClosed ?? ShutterClosed,
        Frozen = reading.Frozen ?? Frozen,
        LightHours = reading.LightHours.IsDefaultOrEmpty ? LightHours : reading.LightHours,
        Temperatures = reading.Temperatures.IsDefaultOrEmpty ? Temperatures : reading.Temperatures,
        Faults = reading.FaultsRead ? reading.Faults : Faults,
        FaultsRead = reading.FaultsRead || FaultsRead,
        Manufacturer = reading.Manufacturer ?? Manufacturer,
        Model = reading.Model ?? Model,
        Serial = reading.Serial ?? Serial,
        Firmware = reading.Firmware ?? Firmware,
        ProjectorName = reading.ProjectorName ?? ProjectorName,
        Signal = reading.Signal.Text.Length > 0 || reading.Signal.HasRaster ? reading.Signal : Signal,
        Lens = new LensPositions(
            reading.Lens.ShiftHorizontal ?? Lens.ShiftHorizontal,
            reading.Lens.ShiftVertical ?? Lens.ShiftVertical,
            reading.Lens.Zoom ?? Lens.Zoom,
            reading.Lens.Focus ?? Lens.Focus),
    };

    /// <summary>Same facts (ignores <see cref="LastSeenMonoNs"/>, which moves on every answer): the manager raises a
    /// change only when this is false, so a desk is not woken every poll.</summary>
    public bool SameFacts(DeviceState other) =>
        LinkId == other.LinkId && Liveness == other.Liveness && Online == other.Online && Power == other.Power
        && Input == other.Input && ShutterClosed == other.ShutterClosed && Frozen == other.Frozen
        && LightHours.SequenceEqual(other.LightHours) && Temperatures.SequenceEqual(other.Temperatures)
        && Faults.SequenceEqual(other.Faults) && FaultsRead == other.FaultsRead && Manufacturer == other.Manufacturer
        && Model == other.Model && Serial == other.Serial && Firmware == other.Firmware
        && ProjectorName == other.ProjectorName && Signal == other.Signal && Lens == other.Lens
        && LastProblem == other.LastProblem;

    /// <summary>One line for the context item and the desk: "on · shutter open · input 31 · 1234 h".</summary>
    public string Headline()
    {
        if (Liveness == DeviceLiveness.Unknown)
            return "not heard yet";
        if (Liveness == DeviceLiveness.Gone)
            return LastProblem.Length > 0 ? "gone — " + LastProblem : "gone";
        var parts = new List<string> { PowerWord(Power) };
        if (ShutterClosed.HasValue)
            parts.Add(ShutterClosed.Value ? "shutter closed" : "shutter open");
        if (Frozen == true)
            parts.Add("frozen");
        if (Input is { Length: > 0 })
            parts.Add("input " + Input);
        if (Signal.HasRaster)
            parts.Add(Signal.Width.ToString(CultureInfo.InvariantCulture) + "x" + Signal.Height.ToString(CultureInfo.InvariantCulture));
        if (!LightHours.IsDefaultOrEmpty)
            parts.Add(LightHours[0].ToString(CultureInfo.InvariantCulture) + " h");
        foreach (var f in Faults)
            parts.Add(f.Words);
        if (Liveness == DeviceLiveness.Stale)
            parts.Insert(0, "stale");
        return string.Join(" · ", parts);
    }

    public static string PowerWord(PowerState p) => p switch
    {
        PowerState.Standby => "standby",
        PowerState.WarmingUp => "warming up",
        PowerState.On => "on",
        PowerState.CoolingDown => "cooling down",
        _ => "power unknown",
    };
}

/// <summary>The Show Bus light for a projector (red = wrong now, amber = needs attention, green = fine, grey =
/// unknown). Unknown stays grey — a projector never heard is not "gone".</summary>
public static class DeviceLights
{
    public static Light Of(DeviceState s)
    {
        if (s.Liveness == DeviceLiveness.Unknown)
            return Light.Grey;
        if (s.Liveness == DeviceLiveness.Gone)
            return Light.Red;
        if (s.Faults.Any(f => f.Severity == FaultSeverity.Error))
            return Light.Red;
        if (s.Liveness == DeviceLiveness.Stale || s.Faults.Length > 0
            || s.Power is PowerState.WarmingUp or PowerState.CoolingDown)
            return Light.Amber;
        return Light.Green;
    }
}
