using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PlayMap.App.Link;
using PlayMap.App.ViewModels;
using PlayMap.App.Views;

namespace PlayMap.App;

/// <summary>The desk application: one main window over one <see cref="DeskSession"/>. What the desk
/// connects to first comes from the command line ("--demo", "--connect host:port [--token …]"); without
/// one it opens on the Connect choice.</summary>
public sealed class DeskApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = DeskOptions.Parse(desktop.Args ?? []);
            var session = new DeskSession(a => Dispatcher.UIThread.Post(a));
            var vm = new MainViewModel(session, Infrastructure.DeskPrefs.Load(Infrastructure.DeskPrefs.DefaultPath));
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            var leaving = false;
            desktop.ShutdownRequested += (sender, e) =>
            {
                // The overlay this desk put on a wall comes off before the desk goes: the shutdown waits for the node's answer
                // (bounded), then goes on.
                if (!leaving && vm.Map.HoldsOverlay)
                {
                    leaving = true;
                    e.Cancel = true;
                    _ = LeaveThenShutDownAsync(vm, desktop);
                    return;
                }
                vm.BeginShutdown();
            };
            vm.Start(options);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task LeaveThenShutDownAsync(MainViewModel vm, IClassicDesktopStyleApplicationLifetime desktop)
    {
        using (var within = new CancellationTokenSource(MainViewModel.LeaveWithin))
            await vm.Map.LeaveMapAsync(within.Token);
        vm.BeginShutdown();
        desktop.Shutdown();
    }
}

/// <summary>The desk's command line.</summary>
public sealed record DeskOptions(ConnectChoice? Choice, string? Host, string? Token)
{
    public static DeskOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ConnectChoice? choice = null;
        string? host = null;
        string? token = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    choice = ConnectChoice.Demo;
                    break;
                case "--own":
                    choice = ConnectChoice.OwnEngine;
                    break;
                case "--connect" when i + 1 < args.Count:
                    choice = ConnectChoice.Network;
                    host = args[++i];
                    break;
                case "--token" when i + 1 < args.Count:
                    token = args[++i];
                    break;
            }
        }
        return new DeskOptions(choice, host, token);
    }
}
