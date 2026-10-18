using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>What one arm hook did: its name, the outcome ("done", "cancelled" past its deadline, "failed"), and its words.</summary>
public readonly record struct ArmHookResult(string Name, string Outcome, string Words);

/// <summary>The arm hooks (<see cref="IShowArmHook"/>): SHOW ARM and SHOW DISARM change the show's mode first (armed is the safe side,
/// and no hook can hold it back), then run every registered hook together, off the lane, each until it answers or its deadline on
/// <see cref="EngineServices.ArmTimers"/> passes, and answer once each has: what each did, a hook past its deadline cancelled and
/// named, one that failed named with its words. A node with no hook answers as it always has.</summary>
public sealed partial class EngineCore
{
    /// <summary>Runs every hook as the show arms (<paramref name="arming"/>) or disarms, and answers what each did, in their order.</summary>
    internal async Task<ImmutableArray<ArmHookResult>> RunArmHooksAsync(bool arming, CancellationToken ct)
    {
        var hooks = Services.ArmHooks;
        if (hooks.IsDefaultOrEmpty)
            return [];
        var runs = new Task<ArmHookResult>[hooks.Length];
        for (var i = 0; i < hooks.Length; i++)
            runs[i] = RunArmHookAsync(hooks[i], arming, ct);
        return [.. await Task.WhenAll(runs).ConfigureAwait(false)];
    }

    private async Task<ArmHookResult> RunArmHookAsync(IShowArmHook hook, bool arming, CancellationToken ct)
    {
        var deadline = hook.Deadline < TimeSpan.Zero ? TimeSpan.Zero : hook.Deadline;
        using var expiry = new CancellationTokenSource(deadline, Services.ArmTimers);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, ct);
        var missed = string.Create(CultureInfo.InvariantCulture,
            $"missed its {deadline.TotalMilliseconds:0} ms deadline and was cancelled: the show is {(arming ? "armed" : "disarmed")} without it");
        Task<string> work;
        try
        {
            work = arming ? hook.ArmingAsync(linked.Token) : hook.DisarmingAsync(linked.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ArmHookResult(hook.Name, "failed", Failed(ex, arming));
        }
        // The deadline ends the wait whether or not the hook heeds its token: a hook that ignores it runs on unawaited.
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = expiry.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), expired);
        var first = await Task.WhenAny(work, expired.Task).ConfigureAwait(false);
        if (first != work)
        {
            _ = ObserveLateAsync(hook.Name, work);
            return new ArmHookResult(hook.Name, "cancelled", missed);
        }
        if (work.IsCanceled)
            return new ArmHookResult(hook.Name, "cancelled", expiry.IsCancellationRequested ? missed : $"was cancelled: the show is {(arming ? "armed" : "disarmed")} without it");
        if (work.Exception is { } faulted)
            return new ArmHookResult(hook.Name, "failed", Failed(faulted.GetBaseException(), arming));
        return new ArmHookResult(hook.Name, "done", await work.ConfigureAwait(false) ?? "");
    }

    private static string Failed(Exception ex, bool arming) =>
        $"failed ({ex.Message}): the show is {(arming ? "armed" : "disarmed")} without it";

    /// <summary>A hook left running past its deadline: its end is awaited, and a fault it ends on is journalled, never left unobserved.</summary>
    private async Task ObserveLateAsync(string name, Task<string> work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Journal.Add("fault", $"the arm hook {name} failed after its deadline: {Journal.Describe(ex)}", ok: false);
        }
    }

    /// <summary>The mode's answer with what each hook did: the JSON gains <c>hooks</c> (<c>name</c>, <c>outcome</c>, <c>words</c>) and the
    /// effect names each. The answer of a node with no hook is the one handed in.</summary>
    internal static CommandReply WithArmHooks(CommandReply reply, ImmutableArray<ArmHookResult> results)
    {
        if (results.IsDefaultOrEmpty || !reply.Ok)
            return reply;
        var said = results.Where(r => r.Outcome != "done" || r.Words.Length > 0).ToList();
        var node = reply.Payload is { Length: > 0 } json ? JsonNode.Parse(json) as JsonObject ?? new JsonObject() : new JsonObject();
        var hooks = new JsonArray();
        foreach (var r in results)
            hooks.Add(new JsonObject { ["name"] = r.Name, ["outcome"] = r.Outcome, ["words"] = r.Words });
        node["hooks"] = hooks;
        var effect = said.Count == 0 ? reply.Effect : reply.Effect + " · " + string.Join("; ", said.Select(r => $"{r.Name}: {r.Words}"));
        return reply with { Payload = node.ToJsonString(HookJson), Effect = effect };
    }

    private static readonly JsonSerializerOptions HookJson = new() { WriteIndented = false };
}
