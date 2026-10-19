using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Wire;
using PlayMap.Engine.Machine;

namespace PlayMap.Engine;

/// <summary>wp3-platform's hooks into the engine (EngineCore.Modules.cs): its verbs (MACHINE, MACHINE SET, MACHINE LOCK), the machine
/// block's identity, capabilities, platform and locked keys, the context's machine item, and INDEX's machine section.</summary>
public sealed partial class EngineCore
{
    partial void RouteMachine(VerbRoutes routes)
    {
        routes.Lane("MACHINE", c => MachineStatus(c.Args));
        routes.Lane("MACHINE SET", c => MachineSet(c.Args, c.Session));
        routes.Lane("MACHINE LOCK", c => MachineLock(c.Args, c.Session));
        routes.Index(new MachineIndex(this));
    }

    /// <summary>The platform as the engine reads it back (the claims made through it, its waits' lateness), or null on a node composed
    /// without wp3-platform's part.</summary>
    private RecordedPlatform? Recorded => Services.MachinePlatform as RecordedPlatform;

    /// <summary>A claim's light: green applied, amber refused, grey when nothing was asked (a platform that claims nothing).</summary>
    internal static Light ClaimLight(Claim c) => c.Applied ? Light.Green : c.Asked.Length == 0 ? Light.Grey : Light.Amber;

    /// <summary>The platform's light: grey when it claims nothing, amber when any claim was refused, green otherwise.</summary>
    private Light PlatformLight()
    {
        if (Services.MachinePlatform.Name == "none")
            return Light.Grey;
        var claims = Recorded?.Claims ?? [];
        return claims.Any(c => ClaimLight(c) == Light.Amber) ? Light.Amber : Light.Green;
    }

    /// <summary>What moved from the locked driver set to the one this node runs now (empty with no lock).</summary>
    private System.Collections.Immutable.ImmutableArray<DriverDrift> MachineDrift(NodeIdentity identity) =>
        Services.MachineNode?.Saved.Locked is { } locked ? locked.DriftTo(LockedDriverSet.Of(identity, Services.Wall.UnixMs)) : [];

    /// <summary>MACHINE [STATUS]: the identity, the claims and the drift, in words, with the same facts as JSON.</summary>
    private CommandReply MachineStatus(List<string> args)
    {
        if (args.Count > 1 || (args.Count == 1 && !args[0].Equals("STATUS", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("MACHINE [STATUS]");
        var platform = Services.MachinePlatform;
        var identity = platform.Identity();
        var claims = Recorded?.Claims ?? [];
        var refused = claims.Where(c => ClaimLight(c) == Light.Amber).ToList();
        var drift = MachineDrift(identity);
        var locked = Services.MachineNode?.Saved.Locked is not null;
        var os = identity.Os.Family == EditionFamily.Unknown && identity.Os.Build == 0 ? "the operating system unread"
            : string.Create(CultureInfo.InvariantCulture,
                $"{EditionTable.Name(identity.Os)} {identity.Os.Version} · build {identity.Os.Build}.{identity.Os.Revision} · tier {EditionTable.Word(EditionTable.Tier(identity.Os))}");
        var claimWords = platform.Name == "none" ? platform.Words
            : refused.Count == 0 ? string.Create(CultureInfo.InvariantCulture, $"{claims.Length} claims, every one applied")
            : string.Create(CultureInfo.InvariantCulture, $"{claims.Length} claims, {refused.Count} refused ({string.Join("; ", refused.Select(c => $"{c.What}: {c.Words}"))})");
        var lockWords = !locked ? "no driver set is locked" : drift.IsEmpty ? "the drivers are the locked set" : string.Join("; ", drift.Select(d => d.Words));
        var words = $"{identity.Build.Display} · {os}; {claimWords}; {lockWords}";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("platform", platform.Name);
            w.WritePropertyName("identity");
            WriteIdentity(w, identity);
            WriteClaims(w, claims);
            w.WriteBoolean("locked", locked);
            WriteDrift(w, drift);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>MACHINE SET &lt;word&gt; &lt;value&gt;: kept on the node, applied now where the claim is the process's (QOS, PROCESS,
    /// GPU-PRIORITY) or keep-awake's, else from the next start, saying which. Refused at its value, for a word or value it does not take,
    /// REALTIME without the privilege, and on a node that keeps no settings.</summary>
    private CommandReply MachineSet(List<string> args, SessionInfo session)
    {
        if (args.Count != 2)
            return CommandReply.Fail(PlatformPolicy.Syntax);
        if (Services.MachineNode is not { } node)
            return CommandReply.Fail("this node keeps no settings (it has no node home): MACHINE SET has nowhere to keep the claim");
        var platform = Services.MachinePlatform;
        var row = PlatformPolicy.Find(args[0]);
        var realtime = args[1].Trim().Equals("REALTIME", StringComparison.OrdinalIgnoreCase);
        var privileged = realtime && (platform.Capabilities().Find(MachineCapabilities.RealtimePrivilege)?.Supported ?? false);
        var saved = node.Saved;
        if (!PlatformPolicy.TrySet(saved, args[0], args[1], privileged, out var next, out var refusal))
            return CommandReply.Fail(refusal);
        try
        {
            node.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandReply.Fail("the node's settings could not be written: " + ex.Message);
        }
        var word = row!.Word;
        var was = PlatformPolicy.ValueOf(saved, word);
        var asked = PlatformPolicy.ValueOf(next, word);
        Claim? claim = null;
        string applies, words;
        if (word is "QOS" or "PROCESS" or "GPU-PRIORITY")
        {
            var what = word switch { "QOS" => "process.qos", "PROCESS" => "process.priority", _ => "process.gpuPriority" };
            var claims = platform.ClaimProcess(next);
            claim = claims.FirstOrDefault(c => c.What == what) is { What: not null } made ? made : null;
            applies = "now";
            words = platform.Name == "none" ? $"{row.Label} {asked} is kept; nothing is claimed on this node: {platform.Words}"
                : claim is { Applied: true } c ? $"{row.Label} is {asked} now (Windows reads back {c.ReadBack})"
                : $"{row.Label} {asked} is kept, and Windows refused it now: {claim?.Words ?? "no answer"}";
            if (word == "GPU-PRIORITY" && asked == "realtime")
                words += "; the direct queue asks for global realtime at the device's next start";
        }
        else if (word == "AWAKE")
        {
            applies = "now";
            words = asked == "on" ? "the machine is kept awake while an enabled display output is open" : "keep-awake is off: the display and the system sleep on their own timeouts";
        }
        else
        {
            applies = row.Applies;
            words = $"{word} {asked.ToUpperInvariant()} is kept and applies from " + word switch
            {
                "TIMER" => "the render host's next start",
                "GC" => "the show's next arm",
                _ => "each claimed thread's next start",
            };
        }
        Journal.Add("machine", $"machine {word.ToLowerInvariant()} {was} → {asked} ({applies})", session.ClientName, ok: claim is not { Applied: false });
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("word", word);
            w.WriteString("asked", asked);
            w.WriteString("was", was);
            w.WriteString("applies", applies);
            if (claim is { } c)
            {
                w.WritePropertyName("claim");
                WriteClaim(w, c);
            }
            else
            {
                w.WriteNull("claim");
            }
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>MACHINE LOCK [CLEAR]: keeps this node's driver set as the one its shows run on, or forgets it; the set already locked,
    /// or CLEAR with none, is refused with the reason and nothing is written.</summary>
    private CommandReply MachineLock(List<string> args, SessionInfo session)
    {
        var clear = args.Count == 1 && args[0].Equals("CLEAR", StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !clear))
            return CommandReply.Fail("MACHINE LOCK [CLEAR]");
        if (Services.MachineNode is not { } node)
            return CommandReply.Fail("this node keeps no settings (it has no node home): MACHINE LOCK has nowhere to keep the driver set");
        var saved = node.Saved;
        var now = LockedDriverSet.Of(Services.MachinePlatform.Identity(), Services.Wall.UnixMs);
        if (clear && saved.Locked is null)
            return CommandReply.Fail("no driver set is locked");
        if (!clear && saved.Locked is { } locked && locked.SameDrivers(now))
            return CommandReply.Fail("this machine's drivers are already the locked set");
        var drift = saved.Locked?.DriftTo(now) ?? [];
        try
        {
            node.Save(saved with { Locked = clear ? null : now });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandReply.Fail("the node's settings could not be written: " + ex.Message);
        }
        var words = clear ? "the driver set is no longer locked: preflight names no drift"
            : drift.IsEmpty ? string.Create(CultureInfo.InvariantCulture, $"this machine's driver set is locked ({now.Adapters.Length} GPUs, Windows {(now.Os.Length > 0 ? now.Os : "unread")}): preflight names any drift")
            : "this machine's driver set is locked again, taking " + string.Join("; ", drift.Select(d => d.Words));
        Journal.Add("machine", clear ? "driver lock cleared" : "driver set locked", session.ClientName);
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("locked", !clear);
            if (clear)
                w.WriteNull("set");
            else
            {
                w.WritePropertyName("set");
                WriteLockedSet(w, now);
            }
            WriteDrift(w, drift);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>The machine block's identity, capabilities, platform and locked keys, written where the machine part is composed (every
    /// node the exe makes, read back through <see cref="RecordedPlatform"/>); a bare core without it writes none, as before the
    /// package.</summary>
    partial void MachineMachine(Utf8JsonWriter w)
    {
        if (Recorded is null)
            return;
        var platform = Services.MachinePlatform;
        var identity = platform.Identity();
        w.WritePropertyName("identity");
        WriteIdentity(w, identity);

        w.WriteStartArray("capabilities");
        foreach (var p in platform.Capabilities().Probes)
        {
            w.WriteStartObject();
            w.WriteString("feature", p.Feature);
            w.WriteString("luid", p.AdapterLuid == 0 ? "" : "0x" + p.AdapterLuid.ToString("x16", CultureInfo.InvariantCulture));
            w.WriteBoolean("supported", p.Supported);
            w.WriteString("words", p.Words);
            w.WriteString("driver", p.Driver);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartObject("platform");
        w.WriteString("name", platform.Name);
        w.WriteString("words", platform.Words);
        w.WriteString("light", ShowContext.LightWord(PlatformLight()));
        WriteClaims(w, Recorded?.Claims ?? []);
        var settings = Services.MachineNode?.Saved ?? MachineSettings.Default;
        w.WriteStartObject("settings");
        foreach (var d in PlatformPolicy.Defaults)
            w.WriteString(SettingKey(d.Word), PlatformPolicy.ValueOf(settings, d.Word));
        w.WriteEndObject();
        var (waits, late) = Recorded?.Lateness ?? (0, 0);
        w.WriteStartObject("timer");
        w.WriteString("mode", settings.Timer);
        w.WriteNumber("waits", waits);
        w.WriteNumber("lateP99Us", Math.Round(late));
        w.WriteEndObject();
        w.WriteBoolean("awake", Services.MachineNode?.AwakeHeld ?? false);
        w.WriteStartObject("gc");
        w.WriteString("setting", settings.Gc);
        w.WriteString("held", Services.MachineNode?.GcHeld ?? "");
        if (Services.MachineNode?.Pauses is { } pauses)
        {
            w.WriteStartObject("pauses");
            w.WriteNumber("count", pauses.Count);
            w.WriteNumber("p99Ms", Math.Round(pauses.P99Ms, 2));
            w.WriteNumber("maxMs", Math.Round(pauses.MaxMs, 2));
            w.WriteStartArray("buckets");
            for (var i = 0; i < pauses.Buckets.Length; i++)
            {
                w.WriteStartObject();
                if (i < GcPauses.EdgesMs.Length)
                    w.WriteNumber("underMs", GcPauses.EdgesMs[i]);
                else
                    w.WriteNumber("overMs", GcPauses.EdgesMs[^1]);
                w.WriteNumber("count", pauses.Buckets[i]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("pauses");
        }
        w.WriteEndObject();
        w.WriteEndObject();

        if (Services.MachineNode?.Saved.Locked is { } locked)
        {
            w.WritePropertyName("locked");
            w.WriteStartObject();
            w.WriteNumber("at", locked.AtUnixMs);
            w.WritePropertyName("set");
            WriteLockedSet(w, locked);
            WriteDrift(w, MachineDrift(identity));
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("locked");
        }
    }

    /// <summary>The machine item: the OS line, the support tier, PlayMap's build and the drift count, lit by the claims and the drift.
    /// A node whose platform claims nothing adds none (the fleet's machine item already stands for it).</summary>
    partial void ContextMachine(List<ContextItem> items, List<ContextLink> links)
    {
        var platform = Services.MachinePlatform;
        if (Recorded is null || platform.Name == "none")
            return;
        var identity = platform.Identity();
        var drift = MachineDrift(identity);
        var label = identity.Os.Family == EditionFamily.Unknown && identity.Os.Build == 0 ? "Machine platform"
            : $"{EditionTable.Name(identity.Os)} {identity.Os.Version}".TrimEnd();
        var light = drift.IsEmpty ? PlatformLight() : Light.Amber;
        var os = identity.Os.Build == 0 ? platform.Words
            : string.Create(CultureInfo.InvariantCulture, $"build {identity.Os.Build}.{identity.Os.Revision} · tier {EditionTable.Word(EditionTable.Tier(identity.Os))}");
        items.Add(new ContextItem("machine:platform", "machine", label, light,
            string.Create(CultureInfo.InvariantCulture, $"{identity.Build.Informational} · {os} · drift {drift.Length}")));
        if (items.Exists(i => i.Id == "machine"))
            links.Add(new ContextLink("machine:platform", "machine", ContextLinkKinds.Serves, light));
    }

    /// <summary>A MACHINE SET word's key in STATE's settings: "qos", "timer", "mmcss", "process", "cpuSets", "gpuPriority", "awake".</summary>
    private static string SettingKey(string word) => word switch
    {
        "CPUSETS" => "cpuSets",
        "GPU-PRIORITY" => "gpuPriority",
        _ => word.ToLowerInvariant(),
    };

    private static void WriteIdentity(Utf8JsonWriter w, NodeIdentity identity)
    {
        w.WriteStartObject();
        w.WriteStartObject("build");
        w.WriteString("version", identity.Build.Version);
        w.WriteString("informational", identity.Build.Informational);
        w.WriteString("fileVersion", identity.Build.FileVersion);
        w.WriteEndObject();
        var os = identity.Os;
        w.WriteStartObject("os");
        w.WriteString("edition", os.Edition);
        w.WriteString("family", os.FamilyWord);
        w.WriteString("version", os.Version);
        w.WriteNumber("build", os.Build);
        w.WriteNumber("revision", os.Revision);
        w.WriteBoolean("ltsc", os.Ltsc);
        w.WriteBoolean("iot", os.Iot);
        w.WriteString("tier", EditionTable.Word(EditionTable.Tier(os)));
        w.WriteString("name", EditionTable.Name(os));
        w.WriteEndObject();
        var cpu = identity.Cpu;
        w.WriteStartObject("cpu");
        w.WriteString("model", cpu.Model);
        w.WriteNumber("logical", cpu.Logical);
        w.WriteStartArray("byEfficiencyClass");
        foreach (var n in cpu.ByEfficiencyClass)
            w.WriteNumberValue(n);
        w.WriteEndArray();
        w.WriteNumber("vectorBits", cpu.VectorBits);
        w.WriteBoolean("avx512", cpu.Avx512);
        w.WriteBoolean("avx10", cpu.Avx10);
        w.WriteEndObject();
        w.WriteStartArray("adapters");
        foreach (var a in identity.Adapters)
        {
            w.WriteStartObject();
            w.WriteString("luid", a.LuidText);
            w.WriteNumber("vendorId", a.VendorId);
            w.WriteNumber("deviceId", a.DeviceId);
            w.WriteString("name", a.Name);
            w.WriteString("driver", a.Driver);
            w.WriteString("branch", a.Branch);
            w.WriteString("tier", a.TierWord);
            w.WriteString("support", a.SupportWord);
            w.WriteBoolean("drivesOutputs", a.DrivesOutputs);
            w.WriteNumber("dedicatedMb", a.DedicatedMb);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("runtimes");
        w.WriteString("agility", identity.Runtimes.Agility);
        w.WriteString("ffmpeg", identity.Runtimes.Ffmpeg);
        w.WriteString("syncFirmware", identity.Runtimes.SyncFirmware);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteClaims(Utf8JsonWriter w, IEnumerable<Claim> claims)
    {
        w.WriteStartArray("claims");
        foreach (var c in claims)
            WriteClaim(w, c);
        w.WriteEndArray();
    }

    private static void WriteClaim(Utf8JsonWriter w, Claim c)
    {
        w.WriteStartObject();
        w.WriteString("what", c.What);
        w.WriteString("asked", c.Asked);
        w.WriteBoolean("applied", c.Applied);
        w.WriteString("readBack", c.ReadBack);
        w.WriteString("words", c.Words);
        w.WriteString("light", ShowContext.LightWord(ClaimLight(c)));
        w.WriteEndObject();
    }

    private static void WriteDrift(Utf8JsonWriter w, IEnumerable<DriverDrift> drift)
    {
        w.WriteStartArray("drift");
        foreach (var d in drift)
        {
            w.WriteStartObject();
            w.WriteString("item", d.Item);
            w.WriteString("what", d.What);
            w.WriteString("locked", d.Locked);
            w.WriteString("now", d.Now);
            w.WriteString("words", d.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteLockedSet(Utf8JsonWriter w, LockedDriverSet set)
    {
        w.WriteStartObject();
        w.WriteString("os", set.Os);
        w.WriteString("playmap", set.PlayMap);
        w.WriteString("agility", set.Agility);
        w.WriteString("ffmpeg", set.Ffmpeg);
        w.WriteString("syncFirmware", set.SyncFirmware);
        w.WriteStartArray("adapters");
        foreach (var a in set.Adapters)
        {
            w.WriteStartObject();
            w.WriteNumber("vendorId", a.VendorId);
            w.WriteNumber("deviceId", a.DeviceId);
            w.WriteString("name", a.Name);
            w.WriteString("driver", a.Driver);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
