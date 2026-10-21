using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.Controls;

/// <summary>A light's shape and word beside its colour, so no light is colour alone: ✓ ok, △ warn, ✕ fault, ■ armed, ○ off, i
/// information, drawn in the light's own status brush. <see cref="Compact"/> shows the shape alone (where the words beside it already
/// name what it is about); the word is then its tooltip and its name for a screen reader.</summary>
public sealed class StatusMark : TextBlock
{
    public static readonly StyledProperty<string?> LightProperty = AvaloniaProperty.Register<StatusMark, string?>(nameof(Light));

    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<StatusMark, bool>(nameof(Compact));

    static StatusMark()
    {
        LightProperty.Changed.AddClassHandler<StatusMark>((m, _) => m.Sync());
        CompactProperty.Changed.AddClassHandler<StatusMark>((m, _) => m.Sync());
    }

    public StatusMark()
    {
        FontSize = 11;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Sync();
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Light
    {
        get => GetValue(LightProperty);
        set => SetValue(LightProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>The shape drawn for the light now.</summary>
    public string Shape => StatusShapes.Shape(Light);

    /// <summary>The word for the light now.</summary>
    public string Word => StatusShapes.Word(Light);

    private void Sync()
    {
        Text = Compact ? Shape : Shape + " " + Word;
        Foreground = LightBrushConverter.Brushes.For(StatusShapes.Token(Light) switch
        {
            "LightGreen" => "green",
            "LightAmber" => "amber",
            "LightRed" => "red",
            _ => "grey",
        });
        ToolTip.SetTip(this, Word);
        AutomationProperties.SetName(this, Word);
    }
}
