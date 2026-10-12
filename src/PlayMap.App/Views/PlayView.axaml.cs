using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;

namespace PlayMap.App.Views;

/// <summary>PLAY. The drags live here (they are pointer gestures); what they do is the view model's: files
/// dropped on the bin are MEDIA IMPORT, a bin item dropped on a playlist is PLAYLIST ADD, an item dragged
/// within its playlist is PLAYLIST MOVE.</summary>
public partial class PlayView : UserControl
{
    /// <summary>What a desk drag carries inside the desk: "item &lt;playlist&gt; &lt;item&gt;" or "source &lt;ref&gt;".</summary>
    public static readonly DataFormat<string> DeskDrag = DataFormat.CreateInProcessFormat<string>("playmap.drag");

    private Point _press;
    private PointerPressedEventArgs? _pressArgs;
    private string? _pending;
    private bool _dragging;

    public PlayView()
    {
        InitializeComponent();
        Bin.AddHandler(DragDrop.DragOverEvent, OnBinDragOver);
        Bin.AddHandler(DragDrop.DropEvent, OnBinDrop);
        AddHandler(PointerPressedEvent, OnPressTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoveTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _pending = null;
            _pressArgs = null;
        }, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnItemsDragOver);
        AddHandler(DragDrop.DropEvent, OnItemsDrop);
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
        ImportButton.Click += OnImportClick;
        NewGeneratorButton.Click += OnNewGeneratorClick;
    }

    private PlayViewModel? Vm => DataContext as PlayViewModel;

    private void OnBinDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnBinDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files)
            return;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        _ = vm.ImportFilesAsync(paths);
        e.Handled = true;
    }

    private void OnImportClick(object? sender, RoutedEventArgs e) => _ = ImportAsync();

    private async Task ImportAsync()
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import media", AllowMultiple = true });
            await vm.ImportFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception ex)
        {
            vm.Report("The file picker failed: " + ex.Message);
        }
    }

    private void OnNewGeneratorClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, vm.NewGenerator);
        NewGeneratorButton.ContextMenu = menu;
        menu.Open(NewGeneratorButton);
    }

    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _press = e.GetPosition(this);
        _pressArgs = e;
        _pending = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext switch
        {
            PlaylistItemViewModel item => $"item {item.PlaylistId} {item.Id}",
            MediaItemViewModel m => $"source media:{m.Id}",
            GeneratorItemViewModel g => $"source generator:{g.Id}",
            _ => null,
        };
    }

    private void OnMoveTunnel(object? sender, PointerEventArgs e)
    {
        if (_pending is not { } payload || _pressArgs is not { } press || _dragging || Point.Distance(e.GetPosition(this), _press) < 6)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pending = null;
        _pressArgs = null;
        _ = DragAsync(press, payload);
    }

    /// <summary>Runs a desk drag to its drop (the drop handlers do the work).</summary>
    private async Task DragAsync(PointerPressedEventArgs press, string payload)
    {
        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DeskDrag, payload));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Vm?.Report("The drag did not complete: " + ex.Message);
        }
        finally
        {
            _dragging = false;
        }
    }

    private static (PlaylistViewModel List, int Index)? DropTarget(DragEventArgs e)
    {
        if (e.Source is not Control c || c.FindAncestorOfType<ListBox>(includeSelf: true) is not { Name: "Items" } box || box.DataContext is not PlaylistViewModel list)
            return null;
        var index = list.Items.Count;
        if (c.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: PlaylistItemViewModel over } container)
        {
            index = list.Items.IndexOf(over);
            if (e.GetPosition(container).Y > container.Bounds.Height / 2)
                index++;
        }
        return (list, index);
    }

    private void OnItemsDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(DeskDrag) is not { } payload || DropTarget(e) is null)
            return;
        e.DragEffects = payload.StartsWith("item", StringComparison.Ordinal) ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnItemsDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.DataTransfer.TryGetValue(DeskDrag) is not { } payload || DropTarget(e) is not { } target)
            return;
        var words = payload.Split(' ');
        if (words[0] == "item" && words.Length == 3 && words[1] == target.List.Id)
        {
            var from = target.List.Items.ToList().FindIndex(i => i.Id == words[2]);
            var to = from >= 0 && from < target.Index ? target.Index - 1 : target.Index;
            if (from != to)
                _ = vm.MoveItemAsync(target.List.Id, words[2], to);
        }
        else if (words[0] == "source" && words.Length == 2 && SourceRef.TryParse(words[1], out var source))
        {
            _ = vm.AddToPlaylistAsync(target.List.Id, source);
        }
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PlaylistItemViewModel item)
        {
            item.Actions[0].Command.Execute(null);
            e.Handled = true;
        }
    }
}
