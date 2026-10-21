using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.Core.Wire;

namespace PlayMap.App.ViewModels;

/// <summary>Owns what the desk is connected to: the link (and its disposal), the engine supervisor for this
/// machine's own engine, and the three ways in — own engine, a node on the network, the demo.</summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly DeskSession _session;
    private readonly Func<EngineEndpoint, IEngineLink?> _links;
    private readonly Func<EngineSupervisor> _makeSupervisor;
    private readonly TimeProvider _time;
    private IAsyncDisposable? _owned;
    private EngineSupervisor? _supervisor;
    private bool _ownsLocalNode;

    /// <param name="session">The desk's session the links are attached to.</param>
    /// <param name="links">Makes the link to an endpoint (this build's engine link; null when the build has none). The manager owns
    /// and disposes what it makes.</param>
    /// <param name="supervisor">Makes the watcher of this machine's own engine, once, the first time the engine does not answer on
    /// loopback; the manager disposes it.</param>
    /// <param name="time">The clock the waits for a link to go live run on.</param>
    public ConnectionManager(DeskSession session, Func<EngineEndpoint, IEngineLink?>? links = null, Func<EngineSupervisor>? supervisor = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _links = links ?? EngineLinks.Create;
        _makeSupervisor = supervisor ?? (() => new EngineSupervisor());
        _time = time ?? TimeProvider.System;
    }

    public event Action<string>? Note;

    /// <summary>What the desk takes back from the node before it lets go of it (another node, the desk closing): awaited, within
    /// <see cref="MainViewModel.LeaveWithin"/>.</summary>
    public Func<CancellationToken, Task>? Leaving { get; set; }

    public async Task ConnectDemoAsync()
    {
        var link = new FakeEngineLink(autoTick: true);
        await AttachAsync(link, link, "Demo node");
    }

    /// <summary>This machine's engine: attach on loopback if one runs; otherwise start it, watch it, and
    /// attach when it answers.</summary>
    public async Task<bool> ConnectOwnAsync(CancellationToken ct)
    {
        var link = _links(EngineEndpoint.Loopback);
        if (link is null)
        {
            Note?.Invoke("This build has no link to a real engine yet — the demo engine is ready instead.");
            return false;
        }
        await AttachAsync(link, link, "This machine");
        _ownsLocalNode = true;
        if (await WaitLiveAsync(link, TimeSpan.FromSeconds(1.5), ct))
            return true;
        if (_supervisor is null)
        {
            _supervisor = _makeSupervisor();
            _supervisor.Note += text => Note?.Invoke(text);
        }
        _supervisor.Start();
        Note?.Invoke("Starting this machine's engine…");
        return await WaitLiveAsync(link, TimeSpan.FromSeconds(15), ct);
    }

    public async Task<bool> ConnectNetworkAsync(EngineEndpoint endpoint, CancellationToken ct)
    {
        var link = _links(endpoint);
        if (link is null)
        {
            Note?.Invoke("This build has no network link yet — the demo engine is ready instead.");
            return false;
        }
        await AttachAsync(link, link, endpoint.ToString());
        return await WaitLiveAsync(link, TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>True once the link is live, within <paramref name="within"/> on the manager's clock. The link's status is read after
    /// its changes are listened to, so a link that goes live between the two is never missed.</summary>
    private async Task<bool> WaitLiveAsync(IEngineLink link, TimeSpan within, CancellationToken ct)
    {
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(LinkStatus s)
        {
            if (s == LinkStatus.Live)
                live.TrySetResult();
        }
        link.StatusChanged += OnStatus;
        try
        {
            if (link.Status == LinkStatus.Live)
                return true;
            await live.Task.WaitAsync(within, _time, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return link.Status == LinkStatus.Live;
        }
        finally
        {
            link.StatusChanged -= OnStatus;
        }
    }

    private async Task AttachAsync(IEngineLink link, IAsyncDisposable owner, string label)
    {
        await ReleaseAsync();
        _owned = owner;
        _session.Attach(link, label);
    }

    private async Task ReleaseAsync()
    {
        if (Leaving is { } leaving && _session.Link is not null)
        {
            using var within = new CancellationTokenSource(MainViewModel.LeaveWithin);
            await leaving(within.Token);
        }
        _session.Detach();
        _ownsLocalNode = false;
        if (_owned is { } old)
        {
            _owned = null;
            await old.DisposeAsync();
        }
    }

    /// <summary>The pairing token of the node the desk is on, read where it lives (never from a line).</summary>
    public string? ReadToken() => _session.Link is FakeEngineLink demo ? demo.PairingToken
        : _ownsLocalNode ? EngineLinks.ReadLocalToken() : null;

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        if (_supervisor is not null)
            await _supervisor.DisposeAsync();
    }
}

/// <summary>The Connect choice: this machine's engine, a node on the network (host:port and token), or the
/// demo engine inside the desk.</summary>
public sealed class ConnectViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;
    private ConnectChoice _choice = ConnectChoice.Demo;
    private string _host = "";
    private string _token = "";
    private string _error = "";
    private bool _isOpen;
    private bool _isBusy;

    public ConnectViewModel(ConnectionManager connections)
    {
        _connections = connections;
        if (EngineLinks.Available)
            _choice = ConnectChoice.OwnEngine;
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy);
        Open = new RelayCommand(() => IsOpen = true);
        Close = new RelayCommand(() => IsOpen = false);
        PickOwn = new RelayCommand(() => Choice = ConnectChoice.OwnEngine);
        PickNetwork = new RelayCommand(() => Choice = ConnectChoice.Network);
        PickDemo = new RelayCommand(() => Choice = ConnectChoice.Demo);
    }

    public AsyncCommand ConnectCommand { get; }

    public RelayCommand Open { get; }

    public RelayCommand Close { get; }

    public RelayCommand PickOwn { get; }

    public RelayCommand PickNetwork { get; }

    public RelayCommand PickDemo { get; }

    /// <summary>Whether this build carries a real link (else only the demo connects).</summary>
    public bool RealLinkAvailable => EngineLinks.Available;

    public ConnectChoice Choice
    {
        get => _choice;
        set
        {
            if (Set(ref _choice, value))
                Raise(nameof(IsOwn), nameof(IsNetwork), nameof(IsDemo), nameof(Sentence));
        }
    }

    public bool IsOwn
    {
        get => _choice == ConnectChoice.OwnEngine;
        set
        {
            if (value)
                Choice = ConnectChoice.OwnEngine;
        }
    }

    public bool IsNetwork
    {
        get => _choice == ConnectChoice.Network;
        set
        {
            if (value)
                Choice = ConnectChoice.Network;
        }
    }

    public bool IsDemo
    {
        get => _choice == ConnectChoice.Demo;
        set
        {
            if (value)
                Choice = ConnectChoice.Demo;
        }
    }

    /// <summary>What pressing Connect is about to do, in one sentence.</summary>
    public string Sentence => _choice switch
    {
        ConnectChoice.OwnEngine => "Attach to this machine's engine on loopback, starting it (and restarting it if it stops) when it is not running.",
        ConnectChoice.Network => "Operate a node across the network: its host, and its pairing token (shown on that node's Network drawer).",
        _ => "Open the demo show inside the desk: every panel works, nothing reaches a projector.",
    };

    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    public string Token
    {
        get => _token;
        set => Set(ref _token, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => Set(ref _isOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                ConnectCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        IsBusy = true;
        Error = "";
        try
        {
            switch (_choice)
            {
                case ConnectChoice.Demo:
                    await _connections.ConnectDemoAsync();
                    IsOpen = false;
                    break;
                case ConnectChoice.OwnEngine:
                    if (await _connections.ConnectOwnAsync(ct))
                        IsOpen = false;
                    else
                        Fail("This machine's engine did not answer.");
                    break;
                case ConnectChoice.Network:
                    if (!EngineEndpoint.TryParse(_host, _token, out var endpoint))
                    {
                        Fail("A host is a name or an address, with an optional :port (9941 by default).");
                        break;
                    }
                    if (await _connections.ConnectNetworkAsync(endpoint, ct))
                        IsOpen = false;
                    else
                        Fail($"{endpoint} did not answer.");
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string why)
    {
        Error = why;
        IsOpen = true;
    }
}
