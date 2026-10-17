using System.Windows.Input;

namespace PlayMap.App.Infrastructure;

/// <summary>A synchronous command. <see cref="CanExecuteChanged"/> is raised by hand (the desk re-evaluates
/// after every STATE push, never on a timer).</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>An asynchronous command: the work runs without blocking the UI thread, a second press while it
/// runs is ignored (unless the command runs every press: <see cref="EveryPress"/>), and an exception never escapes — it goes to
/// the fault sink (the status line), so a command is never an async void that drops its failure on the floor.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, CancellationToken, Task> _run;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onFault;
    private CancellationTokenSource? _cts;

    public AsyncCommand(Func<object?, CancellationToken, Task> run, Func<object?, bool>? canExecute = null, Action<Exception>? onFault = null,
        bool everyPress = false)
    {
        _run = run;
        _canExecute = canExecute;
        _onFault = onFault;
        EveryPress = everyPress;
    }

    public AsyncCommand(Func<CancellationToken, Task> run, Func<bool>? canExecute = null, Action<Exception>? onFault = null, bool everyPress = false)
        : this((_, ct) => run(ct), canExecute is null ? null : _ => canExecute(), onFault, everyPress)
    {
    }

    /// <summary>Every press runs at once, never waiting for the one before (PANIC: a second press is HARD, however slow the first
    /// one's reply).</summary>
    public bool EveryPress { get; }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _cts is not null;

    /// <summary>The task of the last run, so tests and callers can await what a press started.</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    public bool CanExecute(object? parameter) => (EveryPress || _cts is null) && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        Last = RunAsync(parameter);
    }

    /// <summary>Runs the command and returns when it has finished (the same path a button press takes).</summary>
    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return Task.CompletedTask;
        Last = RunAsync(parameter);
        return Last;
    }

    public void Cancel() => _cts?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync(object? parameter)
    {
        if (EveryPress)
        {
            await RunOnceAsync(parameter);
            return;
        }
        using var cts = new CancellationTokenSource();
        _cts = cts;
        RaiseCanExecuteChanged();
        try
        {
            await _run(parameter, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the operator or by the desk closing: nothing was asked of the engine that needs saying.
        }
        catch (Exception ex)
        {
            _onFault?.Invoke(ex);
        }
        finally
        {
            _cts = null;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>One press of a command that runs every press: its own run, nothing held while it waits.</summary>
    private async Task RunOnceAsync(object? parameter)
    {
        try
        {
            await _run(parameter, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onFault?.Invoke(ex);
        }
    }
}
