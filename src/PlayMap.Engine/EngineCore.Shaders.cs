using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Render.Effects;

namespace PlayMap.Engine;

/// <summary>wp3-shaders' hooks into the engine (EngineCore.Modules.cs): its verbs (FX RELEASE), the fx block's quarantine and the
/// compile lane's worker (wp3-shaders-2 continues it). FX CHECK's stages come through <see cref="EngineServices.FxStages"/>. The
/// quarantine is the node's (<see cref="EngineServices.Quarantine"/>, kept in its home), never the project's: FX RELEASE acts on it
/// at once, so no draft holds it.</summary>
public sealed partial class EngineCore
{
    partial void RouteShaders(VerbRoutes routes)
    {
        routes.Lane("FX RELEASE", c => FxRelease(c.Args));
    }

    /// <summary>FX RELEASE &lt;kind&gt;|ALL: lifts a quarantine (a kind by its hash, the name it was quarantined under, or a kind of this
    /// show by its id or name). A kind that is not quarantined, and ALL with none, are refused with the words why and change nothing;
    /// a release the node's home refuses to record is refused with the home's words and changes nothing either.</summary>
    private CommandReply FxRelease(List<string> args)
    {
        const string Syntax = "FX RELEASE <kind>|ALL";
        if (args.Count < 1)
            return CommandReply.Fail(Syntax);
        var quarantine = Services.Quarantine;
        var key = string.Join(' ', args).Trim().Trim('"');
        QuarantineChange change;
        if (key.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            change = quarantine.ReleaseAll();
            if (change.Outcome == QuarantineOutcome.Unchanged)
                return CommandReply.Fail("no kind is quarantined on this node");
        }
        else
        {
            if (QuarantinedKind(key) is not { } entry)
            {
                var held = quarantine.All;
                return CommandReply.Fail(held.IsEmpty
                    ? $"{key} is not quarantined: no kind is quarantined on this node"
                    : $"{key} is not quarantined on this node (quarantined: {string.Join(", ", held.Select(e => e.Kind))})");
            }
            change = quarantine.Release(entry.KindHash);
            if (change.Outcome == QuarantineOutcome.Unchanged)
                return CommandReply.Fail($"{entry.Kind} was released already");
        }
        if (!change.Applied)
            return CommandReply.Fail(change.Words);
        var released = change.Entries;
        FactsChanged();
        var doubt = change.Words.Length > 0 ? $" ({change.Words})" : "";
        foreach (var e in released)
            Journal.Add("fx.release", $"released the quarantine on {e.Kind} ({e.ReasonWord}: {e.Words}){doubt}");
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("released");
            foreach (var e in released)
                w.WriteStringValue(e.Kind);
            w.WriteEndArray();
            w.WriteEndObject();
        });
        var names = string.Join(", ", released.Select(e => e.Kind));
        return CommandReply.Success(payload, $"released {names}: {(released.Length == 1 ? "its slots draw" : "their slots draw")} again{doubt}");
    }

    /// <summary>The quarantined kind a word names: its hash first (each entry's own, so two entries quarantined under one name are told
    /// apart), then the name it was quarantined under, then a kind of this show (by id or name) whose sources are quarantined.</summary>
    private QuarantineEntry? QuarantinedKind(string key)
    {
        var all = Services.Quarantine.All;
        if (all.FirstOrDefault(e => string.Equals(e.KindHash, key, StringComparison.OrdinalIgnoreCase)) is { } hashed)
            return hashed;
        if (all.FirstOrDefault(e => string.Equals(e.Kind, key, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named;
        var catalog = FxCatalog.For(Store.Project.FxAssets);
        var id = key.StartsWith("fa", StringComparison.Ordinal) ? FxCatalog.AssetKindId(key) : key;
        return catalog.FindLoose(id) is { } kind && catalog.Source(kind.Id) is { } source
            && Services.Quarantine.IsQuarantined(source.Hash, out var entry) ? entry : null;
    }

    /// <summary>The show's slots whose kind is quarantined, by the kind's hash.</summary>
    internal ILookup<string, FxSlot> QuarantinedSlots(Project p)
    {
        var held = Services.Quarantine.Hashes;
        if (held.IsEmpty)
            return Array.Empty<FxSlot>().ToLookup(_ => "");
        var catalog = FxCatalog.For(p.FxAssets);
        return Fx.Views(p).Select(v => v.Slot)
            .Select(s => (Slot: s, Hash: catalog.Find(s.Kind) is { } k ? catalog.Source(k.Id)?.Hash : null))
            .Where(x => x.Hash is not null && held.Contains(x.Hash))
            .ToLookup(x => x.Hash!, x => x.Slot);
    }

    partial void FxCompileShaders(Utf8JsonWriter w)
    {
        if (Services.ShaderWorker is not { } worker)
            return;
        var f = worker.Facts;
        w.WriteStartObject("worker");
        w.WriteBoolean("running", f.Running);
        if (f.Pid is { } pid)
            w.WriteNumber("pid", pid);
        else
            w.WriteNull("pid");
        w.WriteNumber("restarts", f.Restarts);
        w.WriteNumber("lastMs", f.LastMs);
        w.WriteNumber("compiled", f.Compiled);
        w.WriteNumber("failed", f.Failed);
        w.WriteStartArray("natives");
        foreach (var n in f.Natives)
        {
            w.WriteStartObject();
            w.WriteString("name", n.Name);
            w.WriteBoolean("ok", n.Ok);
            w.WriteString("words", n.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("words", f.Words);
        w.WriteEndObject();
    }

    partial void FxShaders(Utf8JsonWriter w)
    {
        // A node composed with the shader worker always says its quarantine; any other says one only when it holds a kind.
        var all = Services.Quarantine.All;
        if (Services.ShaderWorker is null && all.IsEmpty)
            return;
        var slots = QuarantinedSlots(Store.Project);
        w.WriteStartArray("quarantine");
        foreach (var e in all)
        {
            w.WriteStartObject();
            w.WriteString("hash", e.KindHash);
            w.WriteString("kind", e.Kind);
            w.WriteString("reason", e.ReasonWord);
            w.WriteString("words", e.Words);
            w.WriteNumber("at", e.AtUnixMs);
            w.WriteString("build", e.Build);
            w.WriteStartArray("slots");
            foreach (var s in slots[e.KindHash])
                w.WriteStringValue(s.Id);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
