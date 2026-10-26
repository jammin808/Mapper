using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.Tests.Ux;

/// <summary>The status rules, from the desk's own files and each theme's tokens: every light in every template and every status drawn
/// in a light's brush is paired with a shape and a word (✓ ok, △ warn, ✕ fault, ■ armed, ○ off, i information), and every light a
/// canvas draws with its shape; ok, warn and fault each at least 3:1 against every surface a light is drawn on in every theme (dark,
/// the dark room, daylight, and each one's blind grey) — the plan's pairwise 3:1 cannot hold for lights legible on a dark panel; the
/// dark palette is Theme/Tokens.axaml's; red marks the air and faults, and among the styles only the air's draw it; the status bar
/// draws a held link (a refused key, its hold running) red, a fault, and a live link not paired amber, as preflight and the context
/// do; no animation repeats faster than 2 Hz; and the desk's brightness and theme are this desk's, kept in desk.json.</summary>
public partial class StatusRuleTests
{
    private static readonly string AppDir = Path.Combine(FindRoot(), "src", "PlayMap.App");

    private static readonly DeskPalette[] Themes =
        [.. new[] { DeskPalette.Dark, DeskPalette.Dim, DeskPalette.Daylight }.SelectMany(p => new[] { p, p.Grey() })];

    /// <summary>The style selectors that touch the air: blackout, PANIC, armed, the GO guard, a high-risk verb (danger).</summary>
    private static readonly string[] AirSelectors =
        ["Button.tool.danger", "Button.blackout", "Button.blackout.on", "Button.danger", "MenuItem.danger", "Button.pill.armed", "Button.panic", "Button.goGuard", "Button.goFlash"];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above the test's folder");
    }

    private static IEnumerable<(string File, string Text)> Axaml() =>
        Directory.EnumerateFiles(AppDir, "*.axaml", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(AppDir, f), File.ReadAllText(f)));

    [GeneratedRegex("<Ellipse(?:(?!/>).)*?Classes=\"light\"(?:(?!/>).)*?Fill=\"\\{Binding ([A-Za-z.]+), Converter=\\{StaticResource LightBrush\\}\\}\"(?:(?!/>).)*?/>(\\s*<ui:StatusMark[^>]*Light=\"\\{Binding ([A-Za-z.]+)\\}\")?", RegexOptions.Singleline)]
    private static partial Regex LightRegex();

    [Fact]
    public void EveryLightInEveryTemplateHasItsShapeAndWord()
    {
        var lights = 0;
        foreach (var (file, text) in Axaml())
        {
            foreach (Match m in LightRegex().Matches(text))
            {
                lights++;
                Assert.True(m.Groups[2].Success, $"{file}: the light bound to {m.Groups[1].Value} has no StatusMark beside it");
                Assert.Equal(m.Groups[1].Value, m.Groups[3].Value);
            }
        }
        Assert.True(lights >= 14, $"{lights} lights found");

        // Each status a shape and a word of its own; every light word STATE uses folds to one of them.
        var statuses = new[] { "green", "amber", "red", "armed", "grey", "info" };
        Assert.Equal(["✓", "△", "✕", "■", "○", "i"], statuses.Select(StatusShapes.Shape));
        Assert.Equal(["ok", "warn", "fault", "armed", "off", "info"], statuses.Select(StatusShapes.Word));
        foreach (var word in new[] { "live", "ready", "good", "stale", "connecting", "fetching", "fair", "down", "missing", "poor", "unplayable" })
            Assert.NotEqual("off", StatusShapes.StatusOf(word));
    }

    /// <summary>The bindings that draw an identity or a mode in the light brush, never a status: an object's own colour (a projector's,
    /// a surface's), a colour parameter's swatch, the window's mode frame (the ARM pill's ■ and the blind tag's words carry it).</summary>
    private static readonly HashSet<string> IdentityBindings = new(StringComparer.Ordinal) { "Color", "Frame", "" };

    [GeneratedRegex(@"\{Binding ?([A-Za-z.]*), Converter=\{StaticResource LightBrush\}")]
    private static partial Regex LightBindingRegex();

    [Fact]
    public void EveryStatusDrawnInALightsBrushHasItsShapeAndWord()
    {
        // In every template: a status binding through the light brush (a fill, a border, a foreground, a background) has a StatusMark of
        // the same light in its file, unless it draws an identity.
        var bindings = 0;
        foreach (var (file, text) in Axaml())
        {
            foreach (Match m in LightBindingRegex().Matches(text))
            {
                var path = m.Groups[1].Value;
                if (IdentityBindings.Contains(path))
                    continue;
                bindings++;
                Assert.True(Regex.IsMatch(text, @"<ui:StatusMark[^>]*Light=""\{Binding " + Regex.Escape(path) + @"\}"""),
                    $"{file}: the status bound to {path} is drawn in a light's brush with no StatusMark of it");
            }
        }
        Assert.True(bindings >= 14, $"{bindings} status bindings found");

        // On the canvases: every light drawn in a status brush has its shape drawn beside it.
        var canvases = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppDir, "Controls"), "*.cs"))
        {
            var name = Path.GetFileName(path);
            // The mark itself; an unused ring control; a preview's accent outline (an identity, never a status); a badge, whose icon is
            // its shape and whose tooltip its words.
            if (name is "StatusMark.cs" or "ScoreRing.cs" or "PreviewSurface.cs" or "Converters.cs" or "BadgeStrip.cs")
                continue;
            var code = File.ReadAllText(path);
            if (!code.Contains("LightBrushConverter.Brushes.For(", StringComparison.Ordinal))
                continue;
            canvases++;
            Assert.True(code.Contains("StatusShapes.Shape(", StringComparison.Ordinal), $"{name} draws a light in a status brush with no shape beside it");
        }
        Assert.True(canvases >= 3, $"{canvases} canvases found");
    }

    [AvaloniaFact]
    public void AStatusMarkDrawsItsShapeAndWordInItsLightsBrush()
    {
        var mark = new StatusMark { Light = "amber" };
        Assert.Equal("△ warn", mark.Text);
        Assert.Same(LightBrushConverter.Brushes.For("amber"), mark.Foreground);
        mark.Compact = true;
        Assert.Equal("△", mark.Text);
        Assert.Equal("warn", Avalonia.Controls.ToolTip.GetTip(mark));
        mark.Light = "red";
        Assert.Equal("✕", mark.Text);
    }

    /// <summary>Each status bar link light is drawn with its shape beside it and its word as the mark's tooltip. Live and paired ✓
    /// green, live and not paired △ amber (heard, its pairing not proved: not yet answered, its key held back or refused), and down, or
    /// held — the far end refused this node's key and its hold runs, a fault — ✕ red, as preflight and the context draw them; stale or
    /// connecting △ amber; not linked ○ grey.</summary>
    [AvaloniaFact]
    public async Task EachLinkStateIsDrawnOnTheStatusBarWithItsShape()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var stagewatch = LinkMark(window, "Stagewatch link");
        var patterns = LinkMark(window, "Patterns link");
        foreach (var (state, paired, light, shape, word) in new[]
        {
            ("live", true, "green", "✓", "ok"),
            ("live", false, "amber", "△", "warn"),
            ("held", false, "red", "✕", "fault"),
            ("down", false, "red", "✕", "fault"),
            ("stale", false, "amber", "△", "warn"),
            ("connecting", false, "amber", "△", "warn"),
            ("off", false, "grey", "○", "off"),
        })
        {
            desk.Link.SetSection("links", "{\"stagewatch\":{\"state\":\"" + state + "\",\"host\":\"10.0.0.6:9924\",\"paired\":" + (paired ? "true" : "false") + "}}");
            DeskHarness.Pump();
            Assert.Equal((state, paired, light, shape, word),
                (state, paired, desk.Main.StagewatchLight, stagewatch.Text, Avalonia.Controls.ToolTip.GetTip(stagewatch) as string));
            Assert.Same(LightBrushConverter.Brushes.For(light), stagewatch.Foreground);
        }

        // The Patterns light reads its pairing by the same arm: live and not paired amber, paired green.
        desk.Link.SetSection("links", """{"patterns":{"state":"live","host":"10.0.0.5:9697","paired":false}}""");
        DeskHarness.Pump();
        Assert.Equal(("amber", "△"), (desk.Main.PatternsLight, patterns.Text));
        desk.Link.SetSection("links", """{"patterns":{"paired":true}}""");
        DeskHarness.Pump();
        Assert.Equal(("green", "✓"), (desk.Main.PatternsLight, patterns.Text));
        window.Close();
    }

    /// <summary>The compact mark drawn beside a status bar light, found by the tooltip of the light's group.</summary>
    private static StatusMark LinkMark(Avalonia.Controls.Window window, string tip) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.StackPanel>()
            .Single(p => string.Equals(Avalonia.Controls.ToolTip.GetTip(p) as string, tip, StringComparison.Ordinal))
            .Children.OfType<StatusMark>().Single();

    /// <summary>Every surface token a light is drawn on: the window, a panel, a card, a toast, a chip and a secondary button (raised 2,
    /// the attention bar's banners among them), a selected row and a checked tool (the accent's tint), and the danger tint.</summary>
    private static readonly string[] LightSurfaces = ["DeskBg", "DeskPanel", "DeskRaised", "DeskRaised2", "DeskAccentSoft", "DeskDangerSoft"];

    [Fact]
    public void OkWarnAndFaultAreEachThreeToOneAgainstEverySurfaceOfEveryTheme()
    {
        // Every background the desk's styles and views give from a token is one of the light surfaces, or a surface no light sits on.
        var drawn = Axaml().SelectMany(f => Regex.Matches(f.Text, @"Background"" Value=""\{StaticResource (Desk[A-Za-z0-9]+)Brush\}""|Background=""\{StaticResource (Desk[A-Za-z0-9]+)Brush\}""")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(LightSurfaces.Append("DeskAccent").Append("DeskDanger").Append("DeskBorderStrong").ToHashSet(StringComparer.Ordinal), drawn);
        var least = double.MaxValue;
        foreach (var theme in Themes)
        {
            foreach (var light in new[] { "LightGreen", "LightAmber", "LightRed" })
            {
                foreach (var surface in LightSurfaces)
                {
                    var ratio = DeskPalette.Contrast(theme.Tokens[light], theme.Tokens[surface]);
                    least = Math.Min(least, ratio);
                    Assert.True(ratio >= 3.0, string.Create(CultureInfo.InvariantCulture, $"{theme.Name}: {light} on {surface} is {ratio:0.00}:1"));
                }
            }
            // The words stay readable on every panel too.
            Assert.True(DeskPalette.Contrast(theme.Tokens["DeskText"], theme.Tokens["DeskPanel"]) >= 7, theme.Name);
            // A theme recolours neutrals, never a meaning: the dark-panel themes share the status tokens.
            if (!theme.IsLight)
            {
                foreach (var token in DeskPalette.StatusTokens)
                    Assert.Equal(DeskPalette.Dark.Tokens[token], theme.Tokens[token]);
            }
        }
        // The blind grey keeps every hue of status and air, and greys the rest.
        var grey = DeskPalette.Dark.Grey();
        foreach (var (token, rgb) in grey.Tokens)
        {
            var kept = DeskPalette.StatusTokens.Contains(token) || DeskPalette.AirTokens.Contains(token);
            var isGrey = ((rgb >> 16) & 0xFF) == ((rgb >> 8) & 0xFF) && ((rgb >> 8) & 0xFF) == (rgb & 0xFF);
            Assert.True(kept ? rgb == DeskPalette.Dark.Tokens[token] : isGrey, token);
        }
        // The least of any light on any surface (the papers name it): red on the dark theme's selected row, in its blind grey.
        Assert.Equal(3.18, Math.Round(least, 2));
    }

    [Fact]
    public void TheDarkPaletteIsTheTokensFile()
    {
        var tokens = File.ReadAllText(Path.Combine(AppDir, "Theme", "Tokens.axaml"));
        var found = Regex.Matches(tokens, "<Color x:Key=\"([A-Za-z0-9]+)\">#([0-9A-Fa-f]{6})</Color>")
            .ToDictionary(m => m.Groups[1].Value, m => uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Assert.Equal(found.OrderBy(k => k.Key, StringComparer.Ordinal), DeskPalette.Dark.Tokens.OrderBy(k => k.Key, StringComparer.Ordinal));
        foreach (var theme in Themes)
            Assert.Equal(found.Keys.Order(StringComparer.Ordinal), theme.Tokens.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheAirsRedIsUsedOnlyByAirStyles()
    {
        var red = new Regex("\\{StaticResource (LightRedBrush|DeskDangerBrush|DeskDangerSoftBrush)\\}");
        var style = new Regex("<Style Selector=\"([^\"]+)\">((?:(?!</Style>).)*)</Style>", RegexOptions.Singleline);
        var uses = 0;
        foreach (var (file, text) in Axaml())
        {
            if (file.EndsWith("Tokens.axaml", StringComparison.Ordinal))
                continue;
            var inStyles = 0;
            foreach (Match s in style.Matches(text))
            {
                var n = red.Count(s.Groups[2].Value);
                if (n == 0)
                    continue;
                inStyles += n;
                uses += n;
                foreach (var selector in s.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                    Assert.True(AirSelectors.Any(a => selector.StartsWith(a, StringComparison.Ordinal)), $"{file}: {selector} draws in the air's red");
            }
            Assert.Equal(inStyles, red.Count(text));
        }
        Assert.True(uses > 0);
    }

    /// <summary>A guard for animations a theme may add later (the desk has none today): none may repeat faster than 2 Hz. The behaviour
    /// of the desk's one alarm, the GO guard's flash, is proven on the clock by <see cref="GoGuardTests"/>.</summary>
    [Fact]
    public void NoAnimationRepeatsFasterThanTwoHertz()
    {
        var animation = new Regex("<Animation[^>]*Duration=\"([0-9:.]+)\"[^>]*>", RegexOptions.Singleline);
        foreach (var (file, text) in Axaml())
        {
            foreach (Match m in animation.Matches(text))
            {
                if (!m.Value.Contains("IterationCount", StringComparison.Ordinal))
                    continue;
                var period = TimeSpan.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.True(period.TotalMilliseconds >= StatusShapes.MinAlarmPeriodMs, $"{file}: an animation repeats every {period.TotalMilliseconds} ms");
            }
        }
        Assert.True(StatusShapes.MinAlarmPeriodMs >= 500);
    }

    [AvaloniaFact]
    public async Task TheThemeAndTheBrightnessAreThisDesksKeptInDeskJson()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-look-");
        var app = Application.Current!;
        try
        {
            var path = Path.Combine(dir.FullName, "desk.json");
            var prefs = DeskPrefs.Load(path);
            Assert.Equal((DeskTheme.Dark, 100), (prefs.Theme, prefs.Brightness));
            var session = new Link.DeskSession(a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
            var main = new ViewModels.MainViewModel(session, prefs);
            await using (main)
            {
                var window = new Views.MainWindow { DataContext = main };
                window.Show();
                DeskHarness.Pump();
                // The dark room: chrome dimmed, the text of the selected and every light at full strength.
                await main.Ux.Themes.Single(t => t.Id == "look.dim").Command.ExecuteAsync();
                Assert.Equal(DeskPalette.Dim.Tokens["DeskTextDim"], DeskLookApply.Drawn(app, "DeskTextDim"));
                Assert.Equal(DeskPalette.Dark.Tokens["DeskText"], DeskLookApply.Drawn(app, "DeskText"));
                Assert.True(DeskPalette.Luminance(DeskPalette.Dim.Tokens["DeskTextDim"]) < DeskPalette.Luminance(DeskPalette.Dark.Tokens["DeskTextDim"]));
                await main.Ux.Themes.Single(t => t.Id == "look.daylight").Command.ExecuteAsync();
                Assert.Equal(DeskPalette.Daylight.Tokens["DeskPanel"], DeskLookApply.Drawn(app, "DeskPanel"));
                Assert.Equal(Avalonia.Styling.ThemeVariant.Light, app.RequestedThemeVariant);
                // Darker by tens down to 20 %, apart from the machine's: the veil over the whole desk.
                for (var i = 0; i < 10; i++)
                    await main.Ux.Darker.Command.ExecuteAsync();
                Assert.Equal(20, prefs.Brightness);
                Assert.False(main.Ux.Darker.IsEnabled);
                Assert.Equal(0.8, main.VeilOpacity, 6);
                Assert.Equal(0.8, MainWindowTests.Find<Avalonia.Controls.Border>(window, "BrightnessVeil").Opacity, 6);
                Assert.False(MainWindowTests.Find<Avalonia.Controls.Border>(window, "BrightnessVeil").IsHitTestVisible);
                window.Close();
            }
            var again = DeskPrefs.Load(path);
            Assert.Equal((DeskTheme.Daylight, 20), (again.Theme, again.Brightness));
        }
        finally
        {
            DeskLookApply.Apply(app, DeskPalette.Dark);
            dir.Delete(recursive: true);
        }
    }
}
