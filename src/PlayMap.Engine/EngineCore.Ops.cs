using System.Collections.Frozen;
using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Devices.Model;
using PlayMap.Engine.Assistant;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine;

/// <summary>The operations verbs — projector control, credentials, the assistant, and the first round's two solvers (SCAN SURFACE,
/// PROJECTOR SOLVE) — whose work waits on a projector, a model, a person or a long computation rather than the lane alone. They are
/// answered off the lane: what they change in the project still goes through the lane (and the store), but a projector's reply, a
/// model's answer or a solve is awaited where it holds nobody else up, so their receipts are facts (the projector observed it; the
/// model answered; the solve was applied) rather than "accepted". Pairing, refusals, faults and the journal follow the router's own
/// rules.</summary>
public sealed partial class EngineCore
{
    /// <summary>The first round's operations verbs; a round-2 row goes through its package's route (or is not available yet).</summary>
    private static readonly FrozenSet<string> OpsVerbs = VerbCatalog.All
        .Where(v => v.Round == 1 && (v.Category is VerbCategory.Device or VerbCategory.Assistant || v.Name is "SCAN SURFACE" or "PROJECTOR SOLVE"))
        .Select(v => v.Name)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The transports through which an assistant (or an approval of one) speaks: they never approve, never
    /// ask the assistant, never touch a secret.</summary>
    private static bool IsAssistantTransport(SessionInfo session) => session.Transport is "assistant" or "mcp" or "approval";

    /// <summary>Where the scan and mesh files go: beside the saved project, else the node's project folder, else the folder a hot
    /// backup keeps its primary's show files in.</summary>
    internal string? ProjectFolderForFiles =>
        _projectPath is { } path ? Path.GetDirectoryName(path) : Services.ProjectFolder ?? _filesFolder;

    /// <summary>The folder a hot backup with no project folder of its own keeps the files its primary's show names beside it.</summary>
    private volatile string? _filesFolder;

    /// <summary>True with the answer under way when the line is an operations verb; false sends it to the router.</summary>
    private bool TryOps(WireLine line, SessionInfo session, CancellationToken ct, out Task<CommandReply> reply)
    {
        if (VerbCatalog.Match(line) is not { } m || !OpsVerbs.Contains(m.Spec.Name))
        {
            reply = Task.FromResult(CommandReply.Fail("not an operations verb"));
            return false;
        }
        reply = RunOpsAsync(m.Spec, line.Words.Skip(m.Words).ToList(), line, session, ct);
        return true;
    }

    private async Task<CommandReply> RunOpsAsync(VerbSpec spec, List<string> args, WireLine line, SessionInfo session, CancellationToken ct)
    {
        var shown = OpsShown(spec, line);
        CommandReply reply;
        Exception? fault = null;
        var call = new VerbCall(spec, args, line, session, ct);
        try
        {
            reply = Admission(spec, line, line.Count - args.Count, session) ?? Gate(spec, line, session, call) ?? spec.Category switch
                {
                    VerbCategory.Device => await DeviceVerbAsync(spec, args, line, session, ct).ConfigureAwait(false),
                    VerbCategory.Assistant => await AssistantVerbAsync(spec, args, line, session, ct).ConfigureAwait(false),
                    _ when spec.Name == "PROJECTOR SOLVE" => await ProjectorSolveAsync(call).ConfigureAwait(false),
                    _ => await ScanSurfaceAsync(call).ConfigureAwait(false),
                };
        }
        catch (EditRefusedException refused)
        {
            reply = CommandReply.Fail(refused.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reply = CommandReply.Fail("cancelled — whether it reached the projector or the model is not known; check STATE");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fault = ex;
            reply = CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        try
        {
            if (RowHeldForTests is { } hold)
                await hold(line, call.HoldsAfterRow).ConfigureAwait(false);
            if (fault is not null)
                Journal.Add("fault", $"{spec.Name}: {Redact(Journal.Describe(fault))}", session.ClientName, shown, false);
            else if (!reply.Silent && IsJournalled(spec))
                Journal.Add(reply.Ok ? "verb" : "refused", Redact(reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : line.Raw) : reply.Payload ?? "refused"),
                    session.ClientName, shown, reply.Ok);
        }
        finally
        {
            BeginAfterRow(call.TakeAfterRow());
        }
        return reply;
    }

    /// <summary>An operations line as the journal keeps it: a credential line (it carries a secret) named, never quoted; any other
    /// with every secret the node holds masked.</summary>
    private string OpsShown(VerbSpec spec, WireLine line) =>
        spec.Name.StartsWith("CREDENTIAL", StringComparison.Ordinal) ? spec.Name + " …" : Redact(line.Raw);

    /// <summary>Masks every secret the node holds (the credential store's, its tokens and keys) and anything shaped
    /// like a key.</summary>
    internal string Redact(string? text) =>
        (Services.Assistant?.Redactor() ?? new SecretRedactor(KnownSecrets())).Redact(text);

    private List<string> KnownSecrets()
    {
        var s = Services.Settings;
        var list = new List<string> { s.Token, s.PatternsToken, s.StagewatchKey };
        if (Services.Devices?.Credentials is NodeCredentialStore store)
            list.AddRange(store.Secrets());
        return list;
    }

    /// <summary>The credential store the node keeps its secrets in (the device service's, else the assistant's).</summary>
    private ICredentialStore? Credentials => Services.Devices?.Credentials ?? Services.Assistant?.Credentials;

    /// <summary>STATE's operations row, at the end of the object: "devices", one per projector link in the project's
    /// order. No secret is ever in it.</summary>
    internal void WriteOpsState(Utf8JsonWriter w)
    {
        w.WriteStartArray("devices");
        foreach (var d in Store.Project.Stage.Devices)
            WriteDevice(w, d);
        w.WriteEndArray();
    }

    /// <summary>STATE.cameras: the scan runner's camera views (none for another scan backend).</summary>
    internal void WriteCamerasState(Utf8JsonWriter w)
    {
        if (Services.Scan is Scanning.ScanRunner runner)
        {
            runner.WriteCameras(w);
            return;
        }
        w.WriteStartArray();
        w.WriteEndArray();
    }

    /// <summary>STATE.scan (and SCAN STATUS): the scan runner's own row — id from the start, state and stage, progress,
    /// each projector, the suggested regions — or the seam's summary for another scan backend.</summary>
    internal void WriteScanState(Utf8JsonWriter w)
    {
        if (Services.Scan is Scanning.ScanRunner runner)
            runner.WriteState(w);
        else
            StateWriter.WriteScan(w, Services.Scan.Current);
    }

    /// <summary>SCAN START's receipt: accepted, with the scan's id when the runner gave it one.</summary>
    private CommandReply ScanStarted(string effect)
    {
        if (Services.Scan is not Scanning.ScanRunner { RunningId: { } id })
            return CommandReply.Accepted(effect);
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteString("effect", effect);
            w.WriteString("scan", id);
            w.WriteEndObject();
        });
        return CommandReply.Success(payload, effect) with { Applied = false };
    }

    /// <summary>The context's operations items: each projector link with its light and headline (a projector in error
    /// is red, one with a warning or going stale amber, one never heard grey) and what it controls; proposals waiting
    /// for a person, amber.</summary>
    internal void AddOpsContext(List<ContextItem> items, List<ContextLink> links)
    {
        var devices = Services.Devices;
        foreach (var d in Store.Project.Stage.Devices)
        {
            var state = devices?.State(d.Id);
            var light = state?.Light ?? Light.Grey;
            var words = devices is null ? "projector control is not on this node" : state is null ? "not polled yet" : state.Headline();
            items.Add(new ContextItem("device:" + d.Id, "device", d.Name, light, Redact(words)));
            if (d.ProjectorId.Length > 0)
                links.Add(new ContextLink("device:" + d.Id, "projector:" + d.ProjectorId, "controls", light));
        }
        if (Services.Assistant is { } assistant)
        {
            var pending = assistant.Queue.Pending;
            if (pending.Count > 0)
                items.Add(new ContextItem("assistant", "assistant", "Assistant", Light.Amber,
                    $"{pending.Count} proposal{(pending.Count == 1 ? "" : "s")} wait{(pending.Count == 1 ? "s" : "")} for a person ({string.Join(", ", pending.Select(p => p.Id))})"));
        }
    }
}
