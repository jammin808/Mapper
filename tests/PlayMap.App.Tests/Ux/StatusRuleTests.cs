using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.Tests.Ux;

/// <summary>The status rules, from the desk's own files and each theme's tokens: every light in every template is paired with a shape
/// and a word (✓ ok, △ warn, ✕ fault, ■ armed, ○ off, i information); ok, warn and fault each at least 3:1 against the panels of every
/// theme (dark, the dark room, daylight, and each one's blind grey); the dark palette is Theme/Tokens.axaml's; the air's red is used only
/// by air styles; no animation repeats faster than 2 Hz; and the desk's brightness and theme are this desk's, kept in desk.json.</summary>
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

    [Fact]
    public void OkWarnAndFaultAreEachThreeToOneAgainstEveryThemesPanels()
    {
        foreach (var theme in Themes)
        {
            foreach (var light in new[] { "LightGreen", "LightAmber", "LightRed" })
            {
                foreach (var surface in new[] { "DeskBg", "DeskPanel", "DeskRaised" })
                {
                    var ratio = DeskPalette.Contrast(theme.Tokens[light], theme.Tokens[surface]);
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
        // The GO guard's flash is one event, never a repeat.
        Assert.True(PlayMap.App.ViewModels.Ux.GoGuard.FlashMs < StatusShapes.MinAlarmPeriodMs);
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
