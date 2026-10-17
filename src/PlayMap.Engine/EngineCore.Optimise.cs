using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Optimise;

namespace PlayMap.Engine;

/// <summary>wp2-optimise's hooks into the engine (EngineCore.Modules.cs): MEDIA OPTIMISE (and CANCEL), MEDIA JOBS, MEDIA VERSIONS,
/// MEDIA USE, MEDIA RULES and MEDIA RULE MODE; STATE.media.optimise; the context's optimise item; the check of an item's
/// <c>use</c>.</summary>
public sealed partial class EngineCore
{
    /// <summary>STATE's job rows: the running ones, then the queue's head, then the latest finished.</summary>
    public const int StateJobs = 16;

    partial void RouteOptimise(VerbRoutes routes)
    {
        routes.OffLane("MEDIA OPTIMISE", MediaOptimiseAsync);
        routes.Lane("MEDIA JOBS", _ => MediaJobs());
        routes.Lane("MEDIA VERSIONS", MediaVersions);
        routes.Lane("MEDIA USE", MediaUse);
        routes.Lane("MEDIA RULES", _ => MediaRules());
        routes.Lane("MEDIA RULE", MediaRule);
    }

    // ---- MEDIA OPTIMISE ---------------------------------------------------------------------------------------------------

    private async Task<CommandReply> MediaOptimiseAsync(VerbCall call)
    {
        const string usage = "MEDIA OPTIMISE <media>…|ALL [TO hap|hap-alpha|hap-q|hap-q-alpha] [QUALITY FAST|SHOW] | MEDIA OPTIMISE CANCEL|PAUSE|RESUME [<job>|ALL]";
        if (Services.Optimise is not { } service)
            return CommandReply.Fail("optimise is not running on this node");
        var args = call.Args.ToList();
        if (args.Count == 0)
            return CommandReply.Fail(usage);
        if (args[0].ToUpperInvariant() is "CANCEL" or "PAUSE" or "RESUME")
            return OptimiseSteer(service, args);
        if (Services.ShowGate.Armed)
            return CommandReply.Fail("optimise runs in rehearsal, not on an armed node — disarm first");
        string? to = null;
        var quality = Store.Project.Optimise.Quality;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals("TO", StringComparison.OrdinalIgnoreCase))
            {
                to = args[i + 1].ToLowerInvariant();
                if (!OptimiseTargets.TryCodec(to, out _))
                    return CommandReply.Fail($"TO {args[i + 1]}: {OptimiseTargets.NotMade(args[i + 1])}");
                args.RemoveRange(i, 2);
                i--;
            }
            else if (args[i].Equals("QUALITY", StringComparison.OrdinalIgnoreCase))
            {
                if (!Recipe.TryQuality(args[i + 1], out quality) || quality == EncodeQuality.Archive)
                    return CommandReply.Fail($"QUALITY is FAST or SHOW, not {args[i + 1]}{(quality == EncodeQuality.Archive ? " (Archive arrives next round)" : "")}");
                args.RemoveRange(i, 2);
                i--;
            }
        }
        if (args.Count == 0 || args.Any(a => a.Equals("TO", StringComparison.OrdinalIgnoreCase) || a.Equals("QUALITY", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail(usage);
        var project = Store.Project;
        List<MediaItem> items;
        if (args.Count == 1 && args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            items = [.. project.Media.Where(m => m.Kind == MediaKind.Video)];
        }
        else
        {
            items = [];
            foreach (var a in args)
            {
                if (project.FindMedia(a) is not { } m)
                    return CommandReply.Fail($"no media {a}");
                items.Add(m);
            }
        }
        if (items.Count == 0)
            return CommandReply.Fail("the library holds no video clip to optimise");
        var outcomes = await Task.Run(() => service.Request(items, to, quality), call.Ct);
        var jobs = outcomes.Where(o => o.Job is not null).ToList();
        if (jobs.Count == 0)
        {
            return CommandReply.Fail("nothing to do: " + string.Join("; ", outcomes.Select(o =>
                (project.FindMedia(o.Media)?.Name ?? o.Media) + ": " + o.Why)));
        }
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteStartArray("jobs");
            foreach (var o in jobs)
            {
                w.WriteStartObject();
                w.WriteString("id", o.Job!.Id);
                w.WriteString("media", o.Media);
                w.WriteString("to", o.Job.To);
                w.WriteString("key", o.Job.Key);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("skipped");
            foreach (var o in outcomes.Where(o => o.Job is null))
            {
                w.WriteStartObject();
                w.WriteString("media", o.Media);
                w.WriteString("why", o.Why);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }), string.Create(CultureInfo.InvariantCulture, $"optimising {jobs.Count} clip{(jobs.Count == 1 ? "" : "s")} in the background")) with { Applied = false };
    }

    private CommandReply OptimiseSteer(OptimiseService service, List<string> args)
    {
        var verb = args[0].ToUpperInvariant();
        if (args.Count > 2)
            return CommandReply.Fail($"MEDIA OPTIMISE {verb} [<job>|ALL]");
        var target = args.Count == 2 ? args[1] : "ALL";
        var all = target.Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var (steered, key, past) = verb switch
        {
            "CANCEL" => (service.Cancel(target), "cancelled", "cancelled"),
            "PAUSE" => (service.Hold(target, true), "paused", "paused"),
            _ => (service.Hold(target, false), "resumed", "resumed"),
        };
        if (steered.Why.Length > 0)
            return CommandReply.Fail(steered.Why);
        var done = steered.Jobs;
        if (done.Count == 0)
        {
            return CommandReply.Fail(verb switch
            {
                "CANCEL" => all ? "no job is queued or running" : $"no unfinished job {target}",
                "PAUSE" => all ? "no unfinished job that is not paused already" : $"{target} is not an unfinished job, or is paused already",
                _ => all ? "no job is paused" : $"{target} is not a paused job",
            });
        }
        var pending = steered.Pending;
        var reply = CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray(key);
            foreach (var id in done)
                w.WriteStringValue(id);
            w.WriteEndArray();
            if (verb == "PAUSE")
            {
                // The running job's pause is applied when its worker says it holds (STATE jobs[].held), not when it is asked.
                w.WriteStartArray("pending");
                foreach (var id in pending)
                    w.WriteStringValue(id);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }), pending.Count == 0 ? $"{past} {string.Join(", ", done)}"
            : string.Join("; ", new[]
            {
                done.Count > pending.Count ? $"{past} {string.Join(", ", done.Except(pending))}" : "",
                $"pause asked of {string.Join(", ", pending)}: it holds at its next frame",
            }.Where(p => p.Length > 0)));
        return pending.Count == 0 ? reply : reply with { Applied = false };
    }

    // ---- MEDIA JOBS, MEDIA VERSIONS, MEDIA USE ----------------------------------------------------------------------------

    private CommandReply MediaJobs()
    {
        if (Services.Optimise is not { } service)
            return CommandReply.Fail("optimise is not running on this node");
        return CommandReply.Success(JsonOut(w => WriteOptimise(w, service.View, int.MaxValue, Store.Project.Optimise.Mode)));
    }

    private CommandReply MediaVersions(VerbCall call)
    {
        if (call.Args.Count != 1)
            return CommandReply.Fail("MEDIA VERSIONS <media>");
        if (Store.Project.FindMedia(call.Args[0]) is not { } item)
            return CommandReply.Fail($"no media {call.Args[0]}");
        var media = Services.Media as MediaBackend;
        var formats = media?.Formats ?? BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4;
        var (picked, why) = VersionChoice.Pick(item, formats, h => Services.Media?.PathForHash(h) is not null);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("media", item.Id);
            w.WriteString("use", item.Use);
            w.WriteString("playsHere", picked?.Id ?? "source");
            w.WriteString("why", why);
            w.WriteStartArray("versions");
            foreach (var v in item.Versions)
            {
                w.WriteStartObject();
                w.WriteString("id", v.Id);
                w.WriteString("to", v.To);
                w.WriteString("codec", v.Info.VideoCodec);
                w.WriteString("contentHash", v.ContentHash);
                w.WriteNumber("sizeBytes", v.SizeBytes);
                w.WriteNumber("psnrAvg", v.Quality.PsnrAvg);
                w.WriteNumber("psnrMin", v.Quality.PsnrMin);
                w.WriteNumber("sampled", v.Quality.Sampled);
                w.WriteBoolean("review", v.Review);
                w.WriteBoolean("present", Services.Media?.PathForHash(v.ContentHash) is not null);
                w.WriteString("unplayable", VersionChoice.Unplayable(v, formats, h => Services.Media?.PathForHash(h) is not null) ?? "");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private CommandReply MediaUse(VerbCall call)
    {
        if (call.Args.Count != 2)
            return CommandReply.Fail("MEDIA USE <media> AUTO|SOURCE|<version>");
        if (Edits.Project.FindMedia(call.Args[0]) is not { } item)
            return CommandReply.Fail($"no media {call.Args[0]}");
        var word = call.Args[1];
        var use = word.Equals("AUTO", StringComparison.OrdinalIgnoreCase) ? "auto"
            : word.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) ? "source"
            : item.Versions.FirstOrDefault(v => string.Equals(v.Id, word, StringComparison.Ordinal))?.Id;
        if (use is null)
            return CommandReply.Fail($"{word} is none of AUTO, SOURCE or a version of {item.Name} ({(item.Versions.IsDefaultOrEmpty ? "it has none" : string.Join(", ", item.Versions.Select(v => v.Id)))})");
        if (string.Equals(item.Use, use, StringComparison.Ordinal))
            return CommandReply.Fail($"{item.Name} already uses {use}");
        var r = Edits.Apply($"{item.Name} uses {use}", p => p with { Media = p.Media.Update(item.Id, m => m with { Use = use }) });
        if (!r.Changed)
            return FromEdit(r);
        var onAir = (Services.Media as MediaBackend)?.OpenDecoders().Count(d => d.ItemId == item.Id) ?? 0;
        var effect = onAir == 0
            ? $"takes effect when {item.Id} next opens"
            : string.Create(CultureInfo.InvariantCulture, $"takes effect when {item.Id} next opens; it is open in {onAir} decoder{(onAir == 1 ? "" : "s")}, which keep{(onAir == 1 ? "s" : "")} playing what they opened");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "applied");
            w.WriteString("media", item.Id);
            w.WriteString("use", use);
            w.WriteString("effect", effect);
            w.WriteEndObject();
        }), effect);
    }

    // ---- MEDIA RULES, MEDIA RULE MODE ---------------------------------------------------------------------------------------

    private CommandReply MediaRules()
    {
        var o = Store.Project.Optimise;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("mode", ModeWord(o.Mode));
            w.WriteString("quality", Recipe.QualityWord(o.Quality));
            w.WriteStartArray("rules");
            foreach (var r in o.Rules)
            {
                w.WriteStartObject();
                w.WriteString("name", r.Name);
                w.WriteString("to", r.To);
                w.WriteString("mode", r.Mode.ToString().ToLowerInvariant());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("builtIn");
            foreach (var name in OptimisePlanner.BuiltIn)
                w.WriteStringValue(name);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private CommandReply MediaRule(VerbCall call)
    {
        if (call.Args.Count != 2 || !call.Args[0].Equals("MODE", StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail("MEDIA RULE MODE AUTO|NEVER");
        OptimiseMode mode;
        if (call.Args[1].Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            mode = OptimiseMode.Auto;
        else if (call.Args[1].Equals("NEVER", StringComparison.OrdinalIgnoreCase))
            mode = OptimiseMode.Never;
        else
            return CommandReply.Fail($"MEDIA RULE MODE is AUTO or NEVER, not {call.Args[1]} (ASK, with its suggestions, arrives next round)");
        if (Edits.Project.Optimise.Mode == mode)
            return CommandReply.Fail($"the optimise mode is {ModeWord(mode)} already");
        var r = Edits.Apply($"optimise mode {ModeWord(mode)}", p => p with { Optimise = p.Optimise with { Mode = mode } });
        if (!r.Changed)
            return FromEdit(r);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("mode", ModeWord(mode));
            w.WriteString("effect", mode == OptimiseMode.Auto ? "clips imported from now on are optimised as the rules say" : "nothing is optimised unless a person asks");
            w.WriteEndObject();
        }), $"optimise mode {ModeWord(mode)}");
    }

    private static string ModeWord(OptimiseMode mode) => mode.ToString().ToLowerInvariant();

    // ---- STATE and the context ---------------------------------------------------------------------------------------------

    partial void MediaOptimise(Utf8JsonWriter w)
    {
        var mode = Store.Project.Optimise.Mode;
        if (Services.Optimise is not { } service)
        {
            w.WriteNull("optimise");
            return;
        }
        w.WritePropertyName("optimise");
        WriteOptimise(w, service.View, StateJobs, mode);
    }

    private static void WriteOptimise(Utf8JsonWriter w, OptimiseView v, int rows, OptimiseMode mode)
    {
        w.WriteStartObject();
        w.WriteString("mode", ModeWord(mode));
        w.WriteString("governor", v.Governor.State.ToString().ToLowerInvariant());
        w.WriteString("words", v.Governor.Words);
        w.WriteNumber("running", v.Running);
        w.WriteNumber("queued", v.Queued);
        w.WriteNumber("failed", v.Failed);
        w.WriteStartObject("worker");
        if (v.Pid is { } pid)
            w.WriteNumber("pid", pid);
        else
            w.WriteNull("pid");
        w.WriteString("gl", v.Gl);
        w.WriteString("encoder", v.Encoder);
        w.WriteNumber("restarts", v.Restarts);
        w.WriteEndObject();
        w.WriteStartArray("jobs");
        foreach (var j in v.Jobs.Take(rows))
        {
            w.WriteStartObject();
            w.WriteString("id", j.Id);
            w.WriteString("media", j.Media);
            w.WriteString("name", j.Name);
            w.WriteString("to", j.To);
            w.WriteString("state", j.State);
            w.WriteNumber("framesDone", j.FramesDone);
            w.WriteNumber("frames", j.Frames);
            w.WriteNumber("fps", j.Fps);
            w.WriteNumber("etaS", j.Fps > 0 && j.Frames > j.FramesDone ? Math.Round((j.Frames - j.FramesDone) / j.Fps, 1) : 0);
            w.WriteNumber("attempts", j.Attempts);
            w.WriteBoolean("paused", j.Paused);
            w.WriteBoolean("held", j.Held);
            w.WriteString("version", j.Version);
            w.WriteNumber("psnrAvg", j.PsnrAvg);
            w.WriteString("words", j.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    partial void ContextOptimise(List<ContextItem> items, List<ContextLink> links)
    {
        if (Services.Optimise is not { } service)
            return;
        var v = service.View;
        var shown = ShowMedia.Of(Store.Project);
        var failed = v.Jobs.FirstOrDefault(j => j.State == JobStates.Failed && shown.Contains(j.Media));
        if (failed is not null)
        {
            items.Add(new ContextItem("optimise", "optimise", "Optimise", Light.Red, $"{failed.Name}: {failed.Words}"));
            return;
        }
        if (v.Running + v.Queued == 0)
            return;
        var c = CultureInfo.InvariantCulture;
        var words = string.Create(c, $"optimising {v.Running + v.Queued} clip{(v.Running + v.Queued == 1 ? "" : "s")}");
        if (v.Governor.Words.Length > 0)
            words += " — " + v.Governor.Words;
        items.Add(new ContextItem("optimise", "optimise", "Optimise", Light.Green, words));
    }

    partial void ValidateOptimise(Project p, object entity, ref string? why)
    {
        if (why is not null || entity is not MediaItem item)
            return;
        if (item.Use is not ("auto" or "source") && !item.Versions.Any(v => string.Equals(v.Id, item.Use, StringComparison.Ordinal)))
            why = $"{item.Name}: use is auto, source or one of its versions, not \"{item.Use}\"";
    }
}
