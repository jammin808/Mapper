using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Core.Show;

namespace PlayMap.App.Tests;

/// <summary>A surface or projector locked by its own flag greys its inspector's fields with LockGuard's words, and its inspector
/// offers Unlock beside those words, outside the greyed fields: the one line the gate lets through (SET &lt;id&gt; locked false).</summary>
public class InspectorUnlockTests
{
    [AvaloniaTheory]
    [InlineData("surface")]
    [InlineData("projector")]
    public async Task AnObjectLockedByItsOwnFlagIsUnlockedFromItsOwnInspector(string kind)
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var node = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == kind);
        desk.Main.Map.Select(node.Kind, node.Id);
        DeskHarness.Pump();
        Assert.True((await desk.Session.SendAsync($"SET {node.Id} locked true", Ct)).Ok);
        DeskHarness.Pump();

        var inspector = desk.Main.Map.Inspector.Current!;
        Assert.False(inspector.IsEditable);
        var project = desk.Link.CurrentProject;
        var words = kind == "surface" ? LockGuard.FlagWords(project.Stage.Surfaces.Find(node.Id)!) : LockGuard.FlagWords(project.Stage.Projectors.Find(node.Id)!);
        Assert.Equal(words, inspector.EditRefusal);
        Assert.Null(desk.Session.GateWords($"SET {node.Id} locked false"));

        var view = window.GetVisualDescendants().OfType<Views.InspectorView>().Single(v => ReferenceEquals(v.DataContext, desk.Main.Map.Inspector) && v.IsEffectivelyVisible);
        var unlock = view.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "InspectorUnlock");
        Assert.True(unlock.IsEffectivelyVisible);
        var button = unlock.GetVisualDescendants().OfType<Button>().Single();
        Assert.True(button.IsEffectivelyEnabled);
        desk.Link.ClearSent();
        DeskHarness.Click(window, button);
        await DeskHarness.Drain([inspector.Unlock]);
        Assert.Equal([$"SET {node.Id} locked false"], desk.Verbs());
        Assert.True(inspector.IsEditable);
        Assert.False(unlock.IsEffectivelyVisible);
        window.Close();
    }
}
