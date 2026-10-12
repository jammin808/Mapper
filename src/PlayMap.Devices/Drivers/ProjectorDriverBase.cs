using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers;

/// <summary>How long a driver waits. Projectors are slow controllers; the defaults are generous and tests shorten them.</summary>
/// <param name="Connect">To open a connection.</param>
/// <param name="Reply">For an answer to a command or a query.</param>
/// <param name="SetSilence">For an error after a set that a protocol answers with silence when it succeeds (Christie).</param>
/// <param name="CommandGap">Between two commands on one projector (Christie asks for at least 150 ms).</param>
public sealed record DriverTimings(TimeSpan Connect, TimeSpan Reply, TimeSpan SetSilence, TimeSpan CommandGap)
{
    public static readonly DriverTimings Default = new(
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(150));

    /// <summary>Short waits for loopback simulators.</summary>
    public static readonly DriverTimings Fast = new(
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(5));
}

/// <summary>
/// The shape every driver shares: each public command checks the capability (refused with the reason when the
/// protocol has no published word for it), checks the unverified gate (refused unless the link allows it), runs
/// the protocol's own core, turns connection failures into <see cref="CommandStage.Unreached"/> (nothing reached
/// the projector) or <see cref="CommandStage.Sent"/> (the command left, no answer came) and writes one log line —
/// with the link's secret removed as a last fence.
/// </summary>
public abstract class ProjectorDriverBase : IProjectorDriver
{
    private readonly ICredentialStore _credentials;
    private readonly IDeviceLog _log;

    protected ProjectorDriverBase(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings)
    {
        Link = link;
        _credentials = credentials;
        _log = log;
        Timings = timings ?? DriverTimings.Default;
    }

    public ProjectorLink Link { get; }

    public abstract ProjectorProtocol Protocol { get; }

    public abstract DriverProvenance Provenance { get; }

    public abstract ProjectorCapabilities Capabilities { get; }

    /// <summary>An unverified driver gates everything; others gate the model-dependent subset they name.</summary>
    public virtual ProjectorCapabilities Unverified =>
        Provenance == DriverProvenance.Unverified ? Capabilities : ProjectorCapabilities.None;

    public virtual string Caveat(ProjectorCapabilities capability) =>
        Provenance == DriverProvenance.Unverified && (Capabilities & capability) != 0
            ? $"{Label}'s commands here are recalled from the vendor's manual and were not re-verified against a public source"
            : "";

    public virtual IReadOnlyList<string> TestPatternNames => [];

    public virtual bool CanIdentify => (Capabilities & ProjectorCapabilities.TestPatterns) != 0;

    protected DriverTimings Timings { get; }

    protected string Label => ProjectorProtocols.Label(Protocol);

    /// <summary>The link's credential, or null when none is set. Read by handshakes only.</summary>
    protected Credential? Credential => _credentials.Find(Link.CredentialRef);

    // ---- the public commands: gate, run, log ----

    public Task<CommandResult> ConnectAsync(CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.None, "connect", ConnectCoreAsync, ct);

    public Task<CommandResult> SetPowerAsync(bool on, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.Power, on ? "power on" : "power off", c => PowerCoreAsync(on, c), ct);

    public Task<CommandResult> SelectInputAsync(string input, CancellationToken ct)
    {
        var name = "input " + (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult(Logged(CommandResult.Refused(name, "No input named — the protocol's own word for it, e.g. " + InputExample + ".")));
        return RunAsync(ProjectorCapabilities.Input, name, c => InputCoreAsync(input.Trim(), c), ct);
    }

    public Task<CommandResult> SetShutterAsync(bool closed, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.Shutter, closed ? "shutter close" : "shutter open", c => ShutterCoreAsync(closed, c), ct);

    public Task<CommandResult> SetFreezeAsync(bool frozen, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.Freeze, frozen ? "freeze on" : "freeze off", c => FreezeCoreAsync(frozen, c), ct);

    public Task<CommandResult> MoveLensAsync(LensMove move, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.LensMotor, LensWords(move), c => LensMoveCoreAsync(move, c), ct);

    public Task<CommandResult> RecallLensMemoryAsync(int slot, CancellationToken ct)
    {
        var name = "lens memory " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (slot < 1)
            return Task.FromResult(Logged(CommandResult.Refused(name, "Lens memory slots count from 1.")));
        return RunAsync(ProjectorCapabilities.LensMemory, name, c => LensMemoryCoreAsync(slot, c), ct);
    }

    public Task<CommandResult> ShowTestPatternAsync(string pattern, CancellationToken ct)
    {
        var p = (pattern ?? "").Trim().ToLowerInvariant();
        var name = "test pattern " + p;
        if (p.Length == 0)
            return Task.FromResult(Logged(CommandResult.Refused(name, "No pattern named — off, identify, or " + PatternList() + ".")));
        if (p != TestPatterns.Off && p != TestPatterns.Identify && !TestPatternNames.Contains(p, StringComparer.Ordinal))
            return Task.FromResult(Logged(CommandResult.Refused(name, $"'{p}' is not a {Label} pattern — off, identify, or {PatternList()}.")));
        return RunAsync(ProjectorCapabilities.TestPatterns, name, c => TestPatternCoreAsync(p, c), ct);
    }

    public Task<CommandResult> ResetGeometryAsync(CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.GeometryReset, "geometry off", GeometryResetCoreAsync, ct);

    public Task<CommandResult> UploadGeometryAsync(WarpGrid grid, CancellationToken ct)
    {
        if (!grid.IsValid)
            return Task.FromResult(Logged(CommandResult.Refused("geometry upload", "The warp grid needs at least 2×2 points, columns × rows of them.")));
        return RunAsync(ProjectorCapabilities.GeometryUpload, "geometry upload", c => GeometryUploadCoreAsync(grid, c), ct);
    }

    public Task<CommandResult> SetEdgeBlendAsync(EdgeBlendSettings blend, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.EdgeBlend, blend.Enabled ? "edge blend on" : "edge blend off", c => EdgeBlendCoreAsync(blend, c), ct);

    public Task<CommandResult> SetBlackLevelAsync(BlackLevelSettings level, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.BlackLevel, "black level", c => BlackLevelCoreAsync(level, c), ct);

    public Task<CommandResult> SetColorGainsAsync(ColorGains gains, CancellationToken ct) =>
        RunAsync(ProjectorCapabilities.ColorGains, "colour gains", c => ColorGainsCoreAsync(gains, c), ct);

    public Task<DriverReading> ReadStatusAsync(CancellationToken ct) =>
        ReadAsync(ProjectorCapabilities.Status, "status", StatusCoreAsync, ct);

    public Task<DriverReading> ReadLensAsync(CancellationToken ct) =>
        ReadAsync(ProjectorCapabilities.LensPositionRead, "lens read", LensReadCoreAsync, ct);

    public Task<CommandResult> RawAsync(string line, CancellationToken ct)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0)
            return Task.FromResult(Logged(CommandResult.Refused("raw", "Nothing to send — a line in the projector's own grammar.")));
        if (Provenance == DriverProvenance.Unverified && !Link.AllowUnverified)
            return Task.FromResult(Logged(CommandResult.Refused("raw", UnverifiedWords("raw lines"))));
        return RunAsync(ProjectorCapabilities.None, "raw", c => RawCoreAsync(text, c), ct);
    }

    // ---- what a driver overrides: the protocol's own words ----

    protected abstract Task<CommandResult> ConnectCoreAsync(CancellationToken ct);

    protected virtual string InputExample => "the input's code";

    protected virtual Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) => NotPublished("power");

    protected virtual Task<CommandResult> InputCoreAsync(string input, CancellationToken ct) => NotPublished("input");

    protected virtual Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) => NotPublished("shutter");

    protected virtual Task<CommandResult> FreezeCoreAsync(bool frozen, CancellationToken ct) => NotPublished("freeze");

    protected virtual Task<CommandResult> LensMoveCoreAsync(LensMove move, CancellationToken ct) => NotPublished("lens");

    protected virtual Task<CommandResult> LensMemoryCoreAsync(int slot, CancellationToken ct) => NotPublished("lens memory");

    protected virtual Task<CommandResult> TestPatternCoreAsync(string pattern, CancellationToken ct) => NotPublished("test pattern");

    protected virtual Task<CommandResult> GeometryResetCoreAsync(CancellationToken ct) => NotPublished("geometry");

    protected virtual Task<CommandResult> GeometryUploadCoreAsync(WarpGrid grid, CancellationToken ct) => NotPublished("geometry upload");

    protected virtual Task<CommandResult> EdgeBlendCoreAsync(EdgeBlendSettings blend, CancellationToken ct) => NotPublished("edge blend");

    protected virtual Task<CommandResult> BlackLevelCoreAsync(BlackLevelSettings level, CancellationToken ct) => NotPublished("black level");

    protected virtual Task<CommandResult> ColorGainsCoreAsync(ColorGains gains, CancellationToken ct) => NotPublished("colour gains");

    protected virtual Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        Task.FromResult(new DriverReading(CommandResult.Refused("status", NoWord("status")), null));

    protected virtual Task<DriverReading> LensReadCoreAsync(CancellationToken ct) =>
        Task.FromResult(new DriverReading(CommandResult.Refused("lens read", NoWord("lens read")), null));

    protected abstract Task<CommandResult> RawCoreAsync(string line, CancellationToken ct);

    // ---- helpers for drivers ----

    /// <summary>A reading's state, empty but for the link id.</summary>
    protected DeviceState Blank() => new() { LinkId = Link.Id };

    /// <summary>
    /// One conversation: connect, run <paramref name="body"/>, close. A failure before the command's bytes left is
    /// <see cref="CommandStage.Unreached"/>; after, <see cref="CommandStage.Sent"/> (it may have acted — say so).
    /// </summary>
    protected async Task<CommandResult> ConverseAsync(string command, Func<TcpChannel, CancellationToken, Task<CommandResult>> body, CancellationToken ct)
    {
        TcpChannel? channel = null;
        try
        {
            channel = await TcpChannel.ConnectAsync(Link.Host, Link.EffectivePort, Timings.Connect, ct).ConfigureAwait(false);
            return await body(channel, ct).ConfigureAwait(false);
        }
        catch (DeviceIoException e)
        {
            return channel is { CommandWritten: true }
                ? CommandResult.Sent(command, "sent, but " + e.Message + " — it may have acted")
                : CommandResult.Unreached(command, e.Message);
        }
        finally
        {
            if (channel is not null)
                await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary><see cref="ConverseAsync"/> for a read: a failure is a reading with no state.</summary>
    protected async Task<DriverReading> ConverseReadAsync(string command, Func<TcpChannel, CancellationToken, Task<DriverReading>> body, CancellationToken ct)
    {
        TcpChannel? channel = null;
        try
        {
            channel = await TcpChannel.ConnectAsync(Link.Host, Link.EffectivePort, Timings.Connect, ct).ConfigureAwait(false);
            return await body(channel, ct).ConfigureAwait(false);
        }
        catch (DeviceIoException e)
        {
            return new DriverReading(channel is { CommandWritten: true }
                ? CommandResult.Sent(command, "asked, but " + e.Message)
                : CommandResult.Unreached(command, e.Message), null);
        }
        finally
        {
            if (channel is not null)
                await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected Task<CommandResult> NotPublished(string what) => Task.FromResult(CommandResult.Refused(what, NoWord(what)));

    protected string NoWord(string what) => $"{Label} has no published command for {what}.";

    /// <summary>"needs a password" with the place to type it.</summary>
    protected string NoPasswordWords() =>
        $"The projector asks for a password ({Label} authentication is on) — set this link's credential.";

    private string PatternList() => TestPatternNames.Count == 0 ? "no named patterns" : string.Join(", ", TestPatternNames);

    private string UnverifiedWords(string what)
    {
        var caveat = Caveat(ProjectorCapabilities.None);
        if (caveat.Length == 0)
            caveat = $"{Label}'s word for it is not confirmed by a public document";
        return $"{char.ToUpperInvariant(what[0])}{what[1..]} through {Label} is unverified — {caveat}. Allow unverified commands on this link only after testing them on the model.";
    }

    private CommandResult? Refusal(ProjectorCapabilities capability, string command)
    {
        if (capability == ProjectorCapabilities.None)
            return Provenance == DriverProvenance.Unverified && !Link.AllowUnverified && command != "connect"
                ? CommandResult.Refused(command, UnverifiedWords(command))
                : null;
        if ((Capabilities & capability) == 0)
            return CommandResult.Refused(command, NoWord(CapabilityWords.Word(capability)));
        if ((Unverified & capability) != 0 && !Link.AllowUnverified)
        {
            var caveat = Caveat(capability);
            return CommandResult.Refused(command,
                $"{CapabilityWords.Word(capability)} through {Label} is unverified — {(caveat.Length > 0 ? caveat : "no public document confirms it")}. Allow unverified commands on this link only after testing them on the model.");
        }
        return null;
    }

    private async Task<CommandResult> RunAsync(ProjectorCapabilities capability, string command, Func<CancellationToken, Task<CommandResult>> core, CancellationToken ct)
    {
        var refused = Refusal(capability, command);
        if (refused is not null)
            return Logged(refused);
        CommandResult result;
        try
        {
            result = await core(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // A driver bug or an I/O failure outside a conversation: words for the desk, never a throw through the
            // manager's poll. The message is redacted like every line.
            result = CommandResult.Unreached(command, "failed — " + e.Message);
        }
        return Logged(result with { Command = command });
    }

    private async Task<DriverReading> ReadAsync(ProjectorCapabilities capability, string command, Func<CancellationToken, Task<DriverReading>> core, CancellationToken ct)
    {
        var refused = Refusal(capability, command);
        if (refused is not null)
            return new DriverReading(Logged(refused), null);
        DriverReading reading;
        try
        {
            reading = await core(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // As in RunAsync: a failed read is a reading with no state and the reason in words.
            reading = new DriverReading(CommandResult.Unreached(command, "failed — " + e.Message), null);
        }
        return reading with { Result = Logged(reading.Result with { Command = command }) };
    }

    private CommandResult Logged(CommandResult r)
    {
        var secret = Credential?.Secret;
        var clean = r with { Words = DeviceLog.Redact(r.Words, secret), Reply = DeviceLog.Redact(r.Reply, secret) };
        _log.Write(DeviceLog.Redact($"{Link.Id} {Label} {clean}", secret));
        return clean;
    }

    private static string LensWords(LensMove m)
    {
        var axis = m.Axis switch
        {
            LensAxis.ShiftHorizontal => "shift h",
            LensAxis.ShiftVertical => "shift v",
            LensAxis.Zoom => "zoom",
            _ => "focus",
        };
        var v = m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return m.Absolute ? $"lens {axis} to {v}" : $"lens {axis} by {v}";
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
