using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>A generator's inspector, built from its kind's <see cref="ParamSpec"/> list: one editor per
/// parameter (a slider for a number with a range, a toggle, a choice, a colour swatch, a palette, text or a
/// preset). Every change is GENERATOR SET &lt;id&gt; &lt;param&gt; &lt;value&gt; with the value written in
/// the invariant culture, through the throttle for sliders.</summary>
public sealed class GeneratorInspector : InspectorViewModel
{
    private string _name = "";
    private string _kindName = "";
    private string _words = "";
    private string _kindId = "";

    public GeneratorInspector(DeskSession session, string id) : base(session, id)
    {
        Actions =
        [
            DeskAction.Send(session, "remove", "Remove generator", () => "REMOVE " + Id).Look("trash", danger: true),
        ];
    }

    public override string Icon => "generator";

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public string KindName
    {
        get => _kindName;
        private set => Set(ref _kindName, value);
    }

    public string Words
    {
        get => _words;
        private set => Set(ref _words, value);
    }

    /// <summary>One editor per parameter, in the catalogue's order.</summary>
    public ObservableCollection<ParamEditor> Editors { get; } = [];

    /// <summary>Sends one parameter (what each editor calls).</summary>
    internal void SetParam(string name, string value, bool throttle)
    {
        if (Loading)
            return;
        var line = $"GENERATOR SET {Id} {name} {VerbText.Word(value)}";
        if (throttle)
            Session.Throttle.Post(Id + " param " + name, line);
        else
            Session.Fire(line);
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        var g = p.FindGenerator(Id);
        if (g is null)
            return false;
        var kind = GeneratorCatalog.Find(g.Kind);
        Title = g.Name;
        Subtitle = kind?.Name ?? g.Kind;
        if (Settled("name"))
            Name = g.Name;
        KindName = kind?.Name ?? g.Kind;
        Words = kind?.Words ?? "";
        if (kind is null)
        {
            Editors.Clear();
            return true;
        }
        if (_kindId != kind.Id)
        {
            _kindId = kind.Id;
            Editors.Clear();
            foreach (var spec in kind.Params)
                Editors.Add(ParamEditor.For(this, kind, spec));
        }
        foreach (var e in Editors)
            e.Load(kind.Value(g.Params, e.Spec.Name));
        return true;
    }
}

/// <summary>One parameter's editor. <see cref="Load"/> takes the engine's value (never sending); a change
/// by the operator sends GENERATOR SET.</summary>
public abstract class ParamEditor : ObservableObject
{
    private bool _loading;

    protected ParamEditor(GeneratorInspector owner, ParamSpec spec)
    {
        Owner = owner;
        Spec = spec;
    }

    protected GeneratorInspector Owner { get; }

    public ParamSpec Spec { get; }

    /// <summary>"Cell size in pixels" — the catalogue's words.</summary>
    public string Words => Spec.Words;

    /// <summary>"cell" → "Cell".</summary>
    public string Label => Spec.Name.Length == 0 ? "" : char.ToUpperInvariant(Spec.Name[0]) + Spec.Name[1..];

    public static ParamEditor For(GeneratorInspector owner, GeneratorKind kind, ParamSpec spec)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Type switch
        {
            ParamType.Number or ParamType.Integer => new NumberParamEditor(owner, spec),
            ParamType.Bool => new BoolParamEditor(owner, spec),
            ParamType.Choice => new ChoiceParamEditor(owner, spec),
            ParamType.Color => new ColorParamEditor(owner, spec),
            ParamType.Colors => new ColorsParamEditor(owner, spec),
            _ when spec.Name == "preset" => new PresetParamEditor(owner, spec, PresetParamEditor.PresetsFor(kind)),
            _ => new TextParamEditor(owner, spec),
        };
    }

    public void Load(string value)
    {
        _loading = true;
        try
        {
            OnLoad(value);
        }
        finally
        {
            _loading = false;
        }
    }

    protected abstract void OnLoad(string value);

    protected void Send(string value, bool throttle)
    {
        if (!_loading)
            Owner.SetParam(Spec.Name, value, throttle);
    }
}

/// <summary>A number or a whole number with its range: a slider and a field. Very wide ranges (a zoom to a
/// million) slide on a logarithmic scale so the useful end is not a pixel wide.</summary>
public sealed class NumberParamEditor : ParamEditor
{
    private double _value;
    private string _text = "";

    public NumberParamEditor(GeneratorInspector owner, ParamSpec spec) : base(owner, spec)
    {
        IsLog = spec.Min > 0 && spec.Max / spec.Min > 1000;
    }

    public bool IsInteger => Spec.Type == ParamType.Integer;

    public bool IsLog { get; }

    public double Minimum => IsLog ? Math.Log10(Spec.Min) : Spec.Min;

    public double Maximum => IsLog ? Math.Log10(Spec.Max) : Spec.Max;

    /// <summary>The slider's position (log10 of the value on a log scale).</summary>
    public double Slider
    {
        get => IsLog ? Math.Log10(Math.Max(_value, Spec.Min)) : _value;
        set => Apply(IsLog ? Math.Pow(10, value) : value, throttle: true);
    }

    public double Value => _value;

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value))
                return;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                Apply(d, throttle: true, fromText: true);
        }
    }

    private void Apply(double v, bool throttle, bool fromText = false)
    {
        v = Math.Clamp(v, Spec.Min, Spec.Max);
        if (IsInteger)
            v = Math.Round(v);
        else if (!IsLog)
            v = Math.Round(v, 4);
        if (Math.Abs(v - _value) < 1e-12 && !fromText)
            return;
        _value = v;
        Raise(nameof(Slider), nameof(Value));
        if (!fromText)
            Set(ref _text, Format(v), nameof(Text));
        Send(Format(v), throttle);
    }

    public string Format(double v) => IsInteger
        ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
        : VerbText.Num(IsLog ? Math.Round(v, 6) : v);

    protected override void OnLoad(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return;
        _value = d;
        Raise(nameof(Slider), nameof(Value));
        Set(ref _text, Format(d), nameof(Text));
    }
}

public sealed class BoolParamEditor(GeneratorInspector owner, ParamSpec spec) : ParamEditor(owner, spec)
{
    private bool _value;

    public bool Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
                Send(value ? "true" : "false", throttle: false);
        }
    }

    protected override void OnLoad(string value) => Value = value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

public sealed class ChoiceParamEditor(GeneratorInspector owner, ParamSpec spec) : ParamEditor(owner, spec)
{
    private string? _value;

    public IReadOnlyList<string> Choices => Spec.Choices;

    public string? Value
    {
        get => _value;
        set
        {
            if (value is not null && Set(ref _value, value))
                Send(value, throttle: false);
        }
    }

    protected override void OnLoad(string value) => Value = Spec.Choices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value;
}

/// <summary>One colour: a swatch, a #RRGGBB field and a row of quick picks.</summary>
public sealed class ColorParamEditor(GeneratorInspector owner, ParamSpec spec) : ParamEditor(owner, spec)
{
    private string _hex = "#FFFFFF";

    public static IReadOnlyList<string> Swatches { get; } =
        ["#FFFFFF", "#000000", "#FF2D55", "#FF9500", "#FFD60A", "#34C759", "#00C7BE", "#0A84FF", "#5E5CE6", "#BF5AF2", "#7FD4FF", "#FFE6B0"];

    public IReadOnlyList<string> Choices => Swatches;

    public string Hex
    {
        get => _hex;
        set
        {
            if (!Set(ref _hex, value))
                return;
            Raise(nameof(Brush));
            if (ColorText.TryParse(value, out var rgb))
                Send(ColorText.Format(rgb), throttle: true);
        }
    }

    public IBrush Brush => Controls.LightBrushConverter.Brushes.For(ColorText.TryParse(_hex, out var rgb) ? ColorText.Format(rgb) : "grey");

    public Infrastructure.RelayCommand Pick => new(p =>
    {
        if (p is string hex)
            Hex = hex;
    });

    protected override void OnLoad(string value) => Hex = value;
}

/// <summary>A palette: comma-separated colours, shown as a strip.</summary>
public sealed class ColorsParamEditor(GeneratorInspector owner, ParamSpec spec) : ParamEditor(owner, spec)
{
    private string _text = "";
    private IReadOnlyList<IBrush> _brushes = [];

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value))
                return;
            Rebuild();
            if (Spec.TryNormalise(value, out var normalised, out _))
                Send(normalised, throttle: true);
        }
    }

    public IReadOnlyList<IBrush> Brushes
    {
        get => _brushes;
        private set => Set(ref _brushes, value);
    }

    private void Rebuild() => Brushes = _text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(c => ColorText.TryParse(c, out _)).Select(c => Controls.LightBrushConverter.Brushes.For(c)).ToList();

    protected override void OnLoad(string value) => Text = value;
}

public sealed class TextParamEditor(GeneratorInspector owner, ParamSpec spec) : ParamEditor(owner, spec)
{
    private string _value = "";

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
                Send(value, throttle: true);
        }
    }

    protected override void OnLoad(string value) => Value = value;
}

/// <summary>A preset name for particles and fractals: a list of the known looks, or any name typed.</summary>
public sealed class PresetParamEditor(GeneratorInspector owner, ParamSpec spec, IReadOnlyList<string> presets) : ParamEditor(owner, spec)
{
    private string _value = "";

    public IReadOnlyList<string> Presets { get; } = presets;

    public string Value
    {
        get => _value;
        set
        {
            if (value is not null && Set(ref _value, value))
                Send(value, throttle: false);
        }
    }

    /// <summary>The looks Patterns ships for each family (the engine reads the name; any other is its own).</summary>
    public static IReadOnlyList<string> PresetsFor(GeneratorKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Family switch
        {
            "particles" => ["Snow", "Confetti", "Starfield", "Embers", "Rain", "Fireflies", "Bubbles", "Sparks"],
            "fractal" => kind.Id == "fractal.warp" ? ["Lava", "Ocean", "Smoke", "Aurora"] : ["Classic", "Deep sea", "Ember", "Ice", "Neon"],
            _ => [],
        };
    }

    protected override void OnLoad(string value) => Value = value;
}
