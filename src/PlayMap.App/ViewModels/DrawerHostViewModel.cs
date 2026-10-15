using System.Collections.ObjectModel;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.ViewModels;

/// <summary>One right-hand drawer: its tabs (each a module's view model with a registered view), the tab shown, and
/// its width.</summary>
public sealed class DrawerViewModel : ObservableObject
{
    private DrawerTab? _selected;

    public DrawerViewModel(string key, string title, string icon, string gesture, double width)
    {
        Key = key;
        Title = title;
        Icon = icon;
        Gesture = gesture;
        Width = width;
    }

    public string Key { get; }

    public string Title { get; }

    public string Icon { get; }

    public string Gesture { get; }

    public double Width { get; }

    public ObservableCollection<DrawerTab> Tabs { get; } = [];

    public DrawerTab? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                Raise(nameof(Content));
        }
    }

    /// <summary>The selected tab's view model.</summary>
    public object? Content => _selected?.Content;

    public bool HasTabs => Tabs.Count > 1;

    /// <summary>Adds a tab in its order; the first tab is selected.</summary>
    public void Add(DrawerTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var at = 0;
        while (at < Tabs.Count && Tabs[at].Order <= tab.Order)
            at++;
        Tabs.Insert(at, tab);
        Selected ??= Tabs[0];
        Raise(nameof(HasTabs));
    }

    /// <summary>Selects a tab by key; false when the drawer has none.</summary>
    public bool Select(string key)
    {
        foreach (var t in Tabs)
        {
            if (t.Key == key)
            {
                Selected = t;
                return true;
            }
        }
        return false;
    }
}

/// <summary>The four drawers on the right (Outputs, Inputs, Health, Network): one open at a time, each with tabs, and ⤢ to
/// take the whole workspace. Opening one closes the other; the drawers are desk-local and send nothing themselves.</summary>
public sealed class DrawerHostViewModel : ObservableObject
{
    private DrawerViewModel? _open;
    private bool _isWide;

    public DrawerHostViewModel()
    {
        Outputs = new DrawerViewModel("outputs", "Outputs", "outputs", "Ctrl+Shift+O", 660);
        Inputs = new DrawerViewModel("inputs", "Inputs", "audio", "Ctrl+Shift+I", 560);
        Health = new DrawerViewModel("health", "Health", "info", "Ctrl+Shift+H", 600);
        Network = new DrawerViewModel("network", "Network", "network", "Ctrl+Shift+N", 560);
        Drawers = [Outputs, Inputs, Health, Network];
        Close = new RelayCommand(() => Open = null);
        ToggleWide = new RelayCommand(() => IsWide = !IsWide);
    }

    public DrawerViewModel Outputs { get; }

    public DrawerViewModel Inputs { get; }

    public DrawerViewModel Health { get; }

    public DrawerViewModel Network { get; }

    public IReadOnlyList<DrawerViewModel> Drawers { get; }

    public RelayCommand Close { get; }

    public RelayCommand ToggleWide { get; }

    /// <summary>The drawer shown, or null.</summary>
    public DrawerViewModel? Open
    {
        get => _open;
        set
        {
            if (Set(ref _open, value))
            {
                Raise(nameof(IsOpen), nameof(Width));
                Opened?.Invoke(value);
            }
        }
    }

    public bool IsOpen => _open is not null;

    /// <summary>⤢: the drawer takes the whole workspace.</summary>
    public bool IsWide
    {
        get => _isWide;
        set
        {
            if (Set(ref _isWide, value))
                Raise(nameof(Width));
        }
    }

    /// <summary>The drawer's width (NaN: the whole workspace).</summary>
    public double Width => _isWide ? double.NaN : _open?.Width ?? 0;

    /// <summary>Raised after a drawer opened or closed (null).</summary>
    public event Action<DrawerViewModel?>? Opened;

    public DrawerViewModel? Find(string key) => Drawers.FirstOrDefault(d => d.Key == key);

    /// <summary>Opens a drawer, or closes it when it is the one open.</summary>
    public void Toggle(string key) => Open = _open?.Key == key ? null : Find(key);

    /// <summary>Opens a drawer at a tab.</summary>
    public void Show(string key, string? tab = null)
    {
        var d = Find(key);
        if (d is null)
            return;
        if (tab is not null)
            d.Select(tab);
        Open = d;
    }

    public void Add(DrawerTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Find(tab.Drawer)?.Add(tab);
    }
}
