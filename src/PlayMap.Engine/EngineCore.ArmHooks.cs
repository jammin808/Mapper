using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>What one arm hook did: its name, the outcome ("done", "cancelled" past its deadline, "failed", or "running" behind an
/// answer that does not wait for it), and its words.</summary>
public readonly record struct ArmHookResult(string Name, string Outcome, string Words);

/// <summary>The arm hooks (<see cref="IShowArmHook"/>): SHOW ARM and SHOW DISARM change the show's mode first (armed is the safe side,
/// and no hook can hold it back), then run every registered hook together, off the lane, each until it answers or its deadline on
/// <see cref="EngineServices.ArmTimers"/> passes (held to <see cref="MaxArmHookDeadline"/>), and answer once each has: what each did,
/// a hook past its deadline cancelled and named, one that failed (in its work, its name or its deadline) named with its words; the run
/// itself never faults. A line from the sync leader is answered once the mode has changed, its hooks run behind the answer and each
/// outcome journalled. A node that comes back armed from its recovery record runs its arming hooks once it is up
/// (<see cref="ArmAgainAsync"/>). A node with no hook answers as it always has.</summary>
public sealed partial class EngineCore
{
    /// <summary>The longest an arm hook is waited for: a longer deadline, or an infinite one, is held to it.</summary>
    public static readonly TimeSpan MaxArmHookDeadline = TimeSpan.FromMinutes(5);

    /// <summary>What a hook running behind the sync leader's line says in the answer.</summary>
    private const string BehindWords = "runs behind this answer: its outcome is journalled on this node";

    /// <summary>1 while the show came back armed from the recovery record and its arming hooks have not run since.</summary>
    private int _armAgain;

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

    /// <summary>The arming hooks of a show that came back armed from the recovery record, run once the node is up (the host calls it
    /// after its parts started, since a hook may need the render host): each outcome is journalled as a recovery row. Answers nothing
    /// when the show did not come back armed, when they ran already, or when the show was disarmed since.</summary>
    internal async Task<ImmutableArray<ArmHookResult>> ArmAgainAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _armAgain, 0) == 0 || !_showMode.Armed)
            return [];
        var results = await RunArmHooksAsync(arming: true, ct).ConfigureAwait(false);
        foreach (var r in results)
            Journal.Add("recovery", $"armed again: {r.Name} — {r.Outcome}{(r.Words.Length > 0 ? ": " + r.Words : "")}", ok: r.Outcome == "done");
        return results;
    }

    /// <summary>The hooks run behind a mode's answer (the sync leader's line): the answer names each as running, and each outcome is
    /// journalled when it ends.</summary>
    private CommandReply WithArmHooksBehind(CommandReply reply, bool arming)
    {
        var hooks = Services.ArmHooks;
        if (hooks.IsDefaultOrEmpty || !reply.Ok)
            return reply;
        var verb = arming ? "SHOW ARM" : "SHOW DISARM";
        _ = Task.Run(async () =>
        {
            foreach (var r in await RunArmHooksAsync(arming, CancellationToken.None).ConfigureAwait(false))
                Journal.Add("show", $"{verb}'s arm hook {r.Name}: {(r.Words.Length > 0 ? r.Words : r.Outcome)}", ok: r.Outcome == "done");
        }, CancellationToken.None);
        return WithArmHooks(reply, [.. hooks.Select(h => new ArmHookResult(NameOf(h), "running", BehindWords))]);
    }

    /// <summary>A hook's name, or "hook" when it has none or cannot say it.</summary>
    private static string NameOf(IShowArmHook hook)
    {
        try
        {
            return hook.Name is { Length: > 0 } name ? name : "hook";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "hook";
        }
    }

    /// <summary>A hook's deadline as it is waited for: none below zero, an infinite one and any above the ceiling held to
    /// <see cref="MaxArmHookDeadline"/>.</summary>
    private static TimeSpan Bounded(TimeSpan deadline) =>
        deadline == Timeout.InfiniteTimeSpan || deadline > MaxArmHookDeadline ? MaxArmHookDeadline
        : deadline < TimeSpan.Zero ? TimeSpan.Zero
        : deadline;

    private async Task<ArmHookResult> RunArmHookAsync(IShowArmHook hook, bool arming, CancellationToken ct)
    {
        var name = NameOf(hook);
        CancellationTokenSource? expiry = null;
        CancellationTokenSource? linked = null;
        TimeSpan deadline;
        Task<string> work;
        try
        {
            deadline = Bounded(hook.Deadline);
            expiry = new CancellationTokenSource(deadline, Services.ArmTimers);
            linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, ct);
            work = arming ? hook.ArmingAsync(linked.Token) : hook.DisarmingAsync(linked.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            linked?.Dispose();
            expiry?.Dispose();
            return new ArmHookResult(name, "failed", Failed(ex, arming));
        }
        using (expiry)
        using (linked)
        {
            var missed = string.Create(CultureInfo.InvariantCulture,
                $"missed its {deadline.TotalMilliseconds:0} ms deadline and was cancelled: the show is {(arming ? "armed" : "disarmed")} without it");
            // The deadline ends the wait whether or not the hook heeds its token: a hook that ignores it runs on unawaited.
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = expiry.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), expired);
            var first = await Task.WhenAny(work, expired.Task).ConfigureAwait(false);
            if (first != work)
            {
                _ = ObserveLateAsync(name, work);
                return new ArmHookResult(name, "cancelled", missed);
            }
            if (work.IsCanceled)
                return new ArmHookResult(name, "cancelled", expiry.IsCancellationRequested ? missed : $"was cancelled: the show is {(arming ? "armed" : "disarmed")} without it");
            if (work.Exception is { } faulted)
                return new ArmHookResult(name, "failed", Failed(faulted.GetBaseException(), arming));
            return new ArmHookResult(name, "done", await work.ConfigureAwait(false) ?? "");
        }
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
