using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;
using PlayMap.Core.Content;

namespace PlayMap.App.Tests;

/// <summary>The generator inspector is built from the kind's ParamSpec list — one editor per parameter, of
/// the parameter's type — and every change is GENERATOR SET with the value in the invariant culture, even
/// on a desk whose own culture writes 2,5.</summary>
public class InspectorTests
{
    [AvaloniaFact]
    public async Task TheGeneratorInspectorBuildsOneEditorPerParamAndWritesInvariantValues()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            await using var desk = new DeskHarness();
            var window = MainWindowTests.Open(desk);
            var play = desk.Main.Play;
            play.BinTab = 1;
            play.Selected = play.Generators.Single(g => g.Id == "gn_snow");
            DeskHarness.Pump();

            var inspector = Assert.IsType<GeneratorInspector>(play.Inspector.Current);
            var kind = GeneratorCatalog.Find("particles.field")!;
            Assert.Equal(kind.Params.Count, inspector.Editors.Count);
            for (var i = 0; i < kind.Params.Count; i++)
            {
                var spec = kind.Params[i];
                var editor = inspector.Editors[i];
                Assert.Same(spec, editor.Spec);
                Assert.IsType(spec.Type switch
                {
                    ParamType.Number or ParamType.Integer => typeof(NumberParamEditor),
                    ParamType.Bool => typeof(BoolParamEditor),
                    ParamType.Choice => typeof(ChoiceParamEditor),
                    ParamType.Color => typeof(ColorParamEditor),
                    ParamType.Colors => typeof(ColorsParamEditor),
                    _ => spec.Name == "preset" ? typeof(PresetParamEditor) : typeof(TextParamEditor),
                }, editor);
            }
            var shown = MainWindowTests.Find<ItemsControl>(window, "Editors");
            Assert.Equal(kind.Params.Count, shown.ItemCount);
            Assert.Equal("1200", ((NumberParamEditor)inspector.Editors.Single(e => e.Spec.Name == "count")).Text);
            Screens.Save(window, "play-generator");

            desk.Link.ClearSent();
            ((NumberParamEditor)inspector.Editors.Single(e => e.Spec.Name == "sizeMin")).Slider = 2.5;
            ((NumberParamEditor)inspector.Editors.Single(e => e.Spec.Name == "gravity")).Text = "-12.75";
            ((BoolParamEditor)inspector.Editors.Single(e => e.Spec.Name == "glow")).Value = true;
            ((ChoiceParamEditor)inspector.Editors.Single(e => e.Spec.Name == "emitter")).Value = "center";
            ((ColorParamEditor)inspector.Editors.Single(e => e.Spec.Name == "background")).Hex = "#102030";
            ((PresetParamEditor)inspector.Editors.Single(e => e.Spec.Name == "preset")).Value = "Confetti";
            await desk.FlushThrottle();

            var sent = desk.Verbs();
            Assert.Contains("GENERATOR SET gn_snow sizeMin 2.5", sent);
            Assert.Contains("GENERATOR SET gn_snow gravity -12.75", sent);
            Assert.Contains("GENERATOR SET gn_snow glow true", sent);
            Assert.Contains("GENERATOR SET gn_snow emitter center", sent);
            Assert.Contains("GENERATOR SET gn_snow background #102030", sent);
            Assert.Contains("GENERATOR SET gn_snow preset Confetti", sent);
            Assert.DoesNotContain(sent, l => l.Contains("2,5", StringComparison.Ordinal));

            var p = desk.Link.CurrentProject.FindGenerator("gn_snow")!.Params;
            Assert.Equal("2.5", p.Get("sizeMin"));
            Assert.Equal("true", p.Get("glow"));
            Assert.Equal("#102030", p.Get("background"));

            // The push that follows does not send anything back (a load never sends).
            desk.Link.ClearSent();
            DeskHarness.Pump();
            await desk.FlushThrottle();
            Assert.Empty(desk.Verbs());
            window.Close();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [AvaloniaFact]
    public async Task TheItemInspectorEditsInOutEndAndRateThroughSet()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var play = desk.Main.Play;
        var show = play.Playlists.Single(p => p.Id == "pl_show");
        show.SelectedItem = show.Items[0];
        DeskHarness.Pump();
        var inspector = Assert.IsType<PlaylistItemInspector>(play.Inspector.Current);
        Assert.Equal("Intro", inspector.Label);
        inspector.InText = "1:02.5";
        inspector.End = PlayMap.Core.Model.EndAction.Hold;
        inspector.Rate = 0.5;
        await desk.FlushThrottle();
        var sent = desk.Verbs();
        Assert.Contains("SET it_intro inNs 62500000000", sent);
        Assert.Contains("SET it_intro end \"hold\"", sent);
        Assert.Contains("SET it_intro rate 0.5", sent);
        var item = desk.Link.CurrentProject.FindPlaylist("pl_show")!.Items[0];
        Assert.Equal(62_500_000_000, item.InNs);
        Assert.Equal(PlayMap.Core.Model.EndAction.Hold, item.End);
        Assert.Equal(0.5, item.Rate);
        Screens.Save(window, "play-item");
        window.Close();
    }
}
