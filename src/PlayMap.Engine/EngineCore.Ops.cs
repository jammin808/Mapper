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

/// <summary>The operations verbs — projector control, credentials, the assistant — whose work waits on a projector,
/// a model or a person rather than the lane alone. They are answered off the lane: what they change in the project
/// still goes through the lane (and the store), but a projector's reply or a model's answer is awaited where it
/// holds nobody else up, so their receipts are facts (the projector observed it; the model answered) rather than
/// "accepted". Pairing, refusals, faults and the journal follow the router's own rules.</summary>
public sealed partial class EngineCore
{
    private static readonly FrozenSet<string> OpsVerbs = VerbCatalog.All
        .Where(v => v.Category is VerbCategory.Device or VerbCategory.Assistant)
        .Select(v => v.Name)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The transports through which an assistant (or an approval of one) speaks: they never approve, never
    /// ask the assistant, never touch a secret.</summary>
    private static bool IsAssistantTransport(SessionInfo session) => session.Transport is "assistant" or "mcp" or "approval";

    /// <summary>Where the scan and mesh files go: beside the saved project, else the node's project folder.</summary>
    internal string? ProjectFolderForFiles =>
        _projectPath is { } path ? Path.GetDirectoryName(path) : Services.ProjectFolder;

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
        // A credential line carries a secret: it is named, never quoted, wherever the line would be kept.
        var shown = spec.Name.StartsWith("CREDENTIAL", StringComparison.Ordinal) ? spec.Name + " …" : Redact(line.Raw);
        CommandReply reply;
        try
        {
            reply = spec.Mutates && !session.Paired
                ? CommandReply.Fail("not paired — this node asks for its pairing token first: AUTH-PROOF on the wire, X-PlayMap-Token on the web")
                : spec.Category == VerbCategory.Device
                    ? await DeviceVerbAsync(spec, args, line, session, ct).ConfigureAwait(false)
                    : await AssistantVerbAsync(spec, args, line, session, ct).ConfigureAwait(false);
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
            Journal.Add("fault", $"{spec.Name}: {Redact(Journal.Describe(ex))}", session.ClientName, shown, false);
            return CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        if (!reply.Silent && spec.Mutates)
            Journal.Add(reply.Ok ? "verb" : "refused", Redact(reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : line.Raw) : reply.Payload ?? "refused"),
                session.ClientName, shown, reply.Ok);
        return reply;
    }

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

    /// <summary>STATE's operations rows, at the end of the object: "devices" (one per projector link, in the
    /// project's order) and "assistant" (null when the node has none). No secret is ever among them.</summary>
    internal void WriteOpsState(Utf8JsonWriter w)
    {
        w.WriteStartArray("devices");
        foreach (var d in Store.Project.Stage.Devices)
            WriteDevice(w, d);
        w.WriteEndArray();
        if (Services.Assistant is { } assistant)
        {
            w.WritePropertyName("assistant");
            assistant.WriteState(w);
        }
        else
            w.WriteNull("assistant");
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
