using System.Globalization;
using PlayMap.Core.Machine;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Install;

/// <summary>wp3-installer's part in the exe's composition (Host/EngineParts.Install.cs): preflight's <c>install</c> section
/// (<see cref="InstallPreflight"/>), read from the same facts STATE's <c>machine.install</c> publishes; and, for an engine the
/// supervisor started, the cause of the stop before it in its journal as it starts — the supervisor's status file names this engine's
/// pid once it has started it, and keeps the last cause across the start.</summary>
public sealed class InstallParts : IEngineExtension
{
    private readonly string? _supervisorFolder;
    private readonly int _pid;

    /// <param name="supervisorFolder">The supervisor's folder for an engine it started (<c>--supervised</c>), else null.</param>
    /// <param name="pid">This engine's process.</param>
    internal InstallParts(string? supervisorFolder, int pid)
    {
        _supervisorFolder = supervisorFolder;
        _pid = pid;
    }

    /// <summary>The supervisor's folder this part reads the status from (null: the engine was not started by the supervisor).</summary>
    internal string? SupervisorFolder => _supervisorFolder;

    /// <summary>The exe's install part.</summary>
    public static readonly EngineExtensionFactory Factory = s => new InstallParts(EngineEntry.StatusFolderFor(s.Options), Environment.ProcessId);

    public string Name => "install";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { PreflightChecks = services.PreflightChecks.Add(new InstallPreflight()) };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_supervisorFolder is not { Length: > 0 } folder)
            return Task.CompletedTask;
        SupervisorStatus? status;
        try
        {
            status = SupervisorStatus.Read(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            engine.Journal.Add("supervisor", "the supervisor's status could not be read as the engine started: " + ex.Message, ok: false);
            return Task.CompletedTask;
        }
        if (StartWords(status, _pid) is { } words)
            engine.Journal.Add("supervisor", words, ok: false);
        return Task.CompletedTask;
    }

    /// <summary>Pure: the journal's words at the start of engine <paramref name="pid"/> for the status the supervisor wrote — the
    /// cause of the stop before it, when the status names this engine and holds one; else null (a first start, or a status of
    /// another engine).</summary>
    internal static string? StartWords(SupervisorStatus? status, int pid) =>
        status is { LastCause.Length: > 0 } s && s.Pid == pid
            ? string.Create(CultureInfo.InvariantCulture, $"the supervisor started this engine after: {s.LastCause} ({s.Restarts} restarts in ten minutes)")
            : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
