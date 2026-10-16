using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using PlayMap.Core.Model;

namespace PlayMap.App.Tests;

/// <summary>PLAY's drags are verbs: files dropped on the library are MEDIA IMPORT (quoted when the name has a
/// space), a source added to a playlist is PLAYLIST ADD, an item moved is PLAYLIST MOVE.</summary>
public class PlayDragTests
{
    [AvaloniaFact]
    public async Task FilesDroppedOnTheLibraryAreImported()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var bin = MainWindowTests.Find<Border>(window, "Bin");
        var at = bin.TranslatePoint(new Point(bin.Bounds.Width / 2, bin.Bounds.Height / 2), window)!.Value;
        // Two real files in this user's own folder, named the way an operator's are.
        var folder = Directory.CreateDirectory(Path.Combine(PlayMap.Core.Media.PreviewRing.DefaultFolder(), "drop-test-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
        var clip = Path.Combine(folder, "Second act.mov");
        var logo = Path.Combine(folder, "Logo2.png");
        await File.WriteAllBytesAsync(clip, [0], Ct);
        await File.WriteAllBytesAsync(logo, [0], Ct);
        var data = new DataTransfer();
        foreach (var path in new[] { clip, logo })
        {
            var item = await window.StorageProvider.TryGetFileFromPathAsync(path);
            Assert.NotNull(item);
            data.Add(DataTransferItem.CreateFile(item));
        }
        window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        for (var i = 0; i < 10 && desk.Verbs().Count(l => l.StartsWith("MEDIA IMPORT", StringComparison.Ordinal)) < 2; i++)
        {
            await Task.Delay(10, Ct);
            DeskHarness.Pump();
        }
        // A path with a space rides quoted, its backslashes (a Windows path's) escaped; one without rides bare, as it is.
        Assert.Contains("MEDIA IMPORT \"" + clip.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"", desk.Verbs());
        Assert.Contains($"MEDIA IMPORT {logo}", desk.Verbs());
        Directory.Delete(folder, recursive: true);
        Assert.Contains(desk.Link.CurrentProject.Media, m => m.Name == "Second act.mov" && m.Kind == MediaKind.Video);
        Assert.Contains(desk.Main.Play.Media, m => m.Name == "Logo2.png" && m.Icon == "image");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AddingAndMovingItemsAreThePlaylistVerbs()
    {
        await using var desk = new DeskHarness();
        var play = desk.Main.Play;
        await play.AddToPlaylistAsync("pl_logo", SourceRef.Generator("gn_julia"), Ct);
        await play.MoveItemAsync("pl_show", "it_outro", 0, Ct);
        DeskHarness.Pump();
        Assert.Equal(["PLAYLIST ADD pl_logo generator:gn_julia", "PLAYLIST MOVE pl_show it_outro 0"], desk.Verbs());
        Assert.Equal(["Logo", "Julia drift"], play.Playlists.Single(p => p.Id == "pl_logo").Items.Select(i => i.Label));
        Assert.Equal("it_outro", play.Playlists.Single(p => p.Id == "pl_show").Items[0].Id);
    }
}
