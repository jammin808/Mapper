using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Fleet;

/// <summary>One output as a machine's health shows it: its light, its measured rate, what it dropped and missed, and the path it
/// presents by. A number not measured is −1.</summary>
public sealed record HealthOutput(string Id, string Name, bool Open, double Fps, long Dropped, long Missed60s, string Path);

/// <summary>A projector as a machine's health shows it: its light and words with its device's facts folded in, and the output that
/// drives it ("" when none does).</summary>
public sealed record HealthProjector(string Id, string Name, Light Light, string Words, string Output);

/// <summary>What a node measured about itself at one instant — the facts, before any light. Every number that was not measured is
/// −1 (and reads grey), never 0.</summary>
public sealed record HealthFacts
{
    public string Node { get; init; } = "";
    public string Name { get; init; } = "";
    public long AtUnixMs { get; init; }
    public string Role { get; init; } = "solo";
    /// <summary>The primary this node backs up ("" when it backs up none).</summary>
    public string Of { get; init; } = "";
    public long Epoch { get; init; }
    public string Build { get; init; } = "";
    public long Rev { get; init; } = -1;
    public ImmutableArray<HealthOutput> Outputs { get; init; } = [];
    public ImmutableArray<HealthProjector> Projectors { get; init; } = [];
    public string Renderer { get; init; } = "";
    public string RenderPath { get; init; } = "";
    public long Resets { get; init; }
    public double CpuPct { get; init; } = -1;
    public double MemMb { get; init; } = -1;
    public double MemFreeMb { get; init; } = -1;
    public double DiskFreeGb { get; init; } = -1;
    public double GpuPct { get; init; } = -1;
    public double GpuTempC { get; init; } = -1;
    /// <summary>Why the GPU reads unknown on this machine ("" when it is read).</summary>
    public string GpuWhy { get; init; } = "";
    public double NetMbps { get; init; } = -1;
    public double UptimeS { get; init; } = -1;
    public string SyncRole { get; init; } = "solo";
    /// <summary>The node a follower takes its clock from ("" when it follows none).</summary>
    public string SyncLeader { get; init; } = "";
    public double SyncUncertaintyMs { get; init; } = -1;
    public double FreewheelS { get; init; }
    public int DevicesTotal { get; init; }
    public int DevicesGreen { get; init; }
    public int DevicesAmber { get; init; }
    public int DevicesRed { get; init; }
    public int MediaItems { get; init; }
    public int MediaMissing { get; init; }
    public double LaneMs { get; init; } = -1;
    public double RenderMs { get; init; } = -1;
}

/// <summary>One category of a machine's health with its light and its words.</summary>
public sealed record HealthCategory(string Name, Light Light, string Words);

/// <summary>A machine's health (Show Bus context, the fleet board, Patterns' Eye): the facts, each category's light by the table of
/// <see cref="HealthLights"/>, the machine's light (the worst known category: red over amber over green; unknown says nothing) and
/// a headline of facts. No secret and no content beyond names and counts: it is an open read, like the hello.</summary>
public sealed record HealthRecord(HealthFacts Facts, ImmutableArray<HealthCategory> Categories, Light Light, string Headline)
{
    /// <summary>The record of some facts: every category graded, the light and the headline computed.</summary>
    public static HealthRecord Of(HealthFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var categories = HealthLights.Grade(facts);
        var light = HealthLights.Worst(categories.Select(c => c.Light));
        return new HealthRecord(facts, categories, light, HealthLights.Headline(facts, categories, light));
    }

    /// <summary>The light of each output, by the same table as the outputs category.</summary>
    public Light LightOf(HealthOutput output) => HealthLights.OutputLight(output);
}

/// <summary>The light table. Every category is present in every record, in this order; a category with nothing measured is grey with
/// words that say why.</summary>
public static class HealthLights
{
    /// <summary>The categories, in the order every record carries them.</summary>
    public static readonly ImmutableArray<string> Names =
        ["outputs", "render", "cpu", "memory", "disk", "gpu", "network", "sync", "devices", "media", "watchdog"];

    /// <summary>CPU at or above these percentages is amber, red.</summary>
    public const double CpuAmberPct = 80, CpuRedPct = 95;

    /// <summary>Free memory under these megabytes is amber, red.</summary>
    public const double MemAmberMb = 1536, MemRedMb = 512;

    /// <summary>Free disk under these gigabytes is amber, red.</summary>
    public const double DiskAmberGb = 10, DiskRedGb = 2;

    /// <summary>GPU load at or above these percentages, or its temperature at or above these degrees, is amber, red.</summary>
    public const double GpuAmberPct = 90, GpuRedPct = 98, GpuAmberC = 80, GpuRedC = 90;

    /// <summary>Network traffic at or above this is amber: a gigabit link near its limit.</summary>
    public const double NetAmberMbps = 900;

    /// <summary>A follower's clock uncertainty above these milliseconds is amber, red.</summary>
    public const double SyncAmberMs = 2, SyncRedMs = 10;

    /// <summary>The control lane's worst tick gap (10 ms ticks) and the render's p95 frame time at or above these are amber, red.</summary>
    public const double LaneAmberMs = 60, LaneRedMs = 250, RenderAmberMs = 40, RenderRedMs = 100;

    public static ImmutableArray<HealthCategory> Grade(HealthFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return
        [
            Outputs(f), Render(f), Cpu(f), Memory(f), Disk(f), Gpu(f), Network(f), Sync(f), Devices(f), Media(f), Watchdog(f),
        ];
    }

    /// <summary>Red over amber over green over grey: an unknown says nothing good or bad.</summary>
    public static Light Worst(IEnumerable<Light> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        var worst = Light.Grey;
        foreach (var l in lights)
        {
            if (l > worst)
                worst = l;
        }
        return worst;
    }

    public static Light OutputLight(HealthOutput o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!o.Open)
            return Light.Red;
        if (o.Dropped > 0 || o.Missed60s > 0)
            return Light.Amber;
        return o.Fps < 0 ? Light.Grey : Light.Green;
    }

    private static HealthCategory Outputs(HealthFacts f)
    {
        if (f.Outputs.IsDefaultOrEmpty)
            return new("outputs", Light.Grey, "no outputs on");
        var lights = f.Outputs.Select(o => (o, Light: OutputLight(o))).ToList();
        var worst = Worst(lights.Select(x => x.Light));
        var first = lights.FirstOrDefault(x => x.Light == worst).o;
        var words = worst switch
        {
            Light.Red => $"{first.Name} not open",
            Light.Amber => Words($"{first.Name}: {first.Dropped} dropped, {first.Missed60s} missed in a minute"),
            Light.Grey => $"{first.Name} not measured yet",
            _ => Words($"{f.Outputs.Length} output{Plural(f.Outputs.Length)} presenting"),
        };
        return new("outputs", worst, words);
    }

    private static HealthCategory Render(HealthFacts f)
    {
        if (f.Renderer.Length == 0 || f.Renderer == "none")
            return new("render", Light.Grey, "no renderer on this node");
        if (f.Resets > 0)
            return new("render", Light.Amber, Words($"{f.Renderer}: the renderer reset {f.Resets} time{Plural((int)Math.Min(f.Resets, 2))}"));
        return new("render", Light.Green, f.RenderPath.Length > 0 ? $"{f.Renderer} · {f.RenderPath}" : f.Renderer);
    }

    private static HealthCategory Cpu(HealthFacts f) => f.CpuPct < 0
        ? new("cpu", Light.Grey, "CPU not sampled on this node")
        : new("cpu", f.CpuPct >= CpuRedPct ? Light.Red : f.CpuPct >= CpuAmberPct ? Light.Amber : Light.Green, Words($"CPU {f.CpuPct:0} %"));

    private static HealthCategory Memory(HealthFacts f) => f.MemFreeMb < 0
        ? new("memory", Light.Grey, "memory not sampled on this node")
        : new("memory", f.MemFreeMb < MemRedMb ? Light.Red : f.MemFreeMb < MemAmberMb ? Light.Amber : Light.Green,
            Words($"{f.MemFreeMb / 1024:0.0} GB free"));

    private static HealthCategory Disk(HealthFacts f) => f.DiskFreeGb < 0
        ? new("disk", Light.Grey, "disk not sampled on this node")
        : new("disk", f.DiskFreeGb < DiskRedGb ? Light.Red : f.DiskFreeGb < DiskAmberGb ? Light.Amber : Light.Green,
            Words($"{f.DiskFreeGb:0.0} GB free on the media disk"));

    private static HealthCategory Gpu(HealthFacts f)
    {
        if (f.GpuPct < 0 && f.GpuTempC < 0)
            return new("gpu", Light.Grey, f.GpuWhy.Length > 0 ? f.GpuWhy : "GPU load and temperature are not readable on this machine");
        var red = f.GpuPct >= GpuRedPct || f.GpuTempC >= GpuRedC;
        var amber = f.GpuPct >= GpuAmberPct || f.GpuTempC >= GpuAmberC;
        var parts = new List<string>();
        if (f.GpuPct >= 0)
            parts.Add(Words($"GPU {f.GpuPct:0} %"));
        if (f.GpuTempC >= 0)
            parts.Add(Words($"{f.GpuTempC:0} °C"));
        return new("gpu", red ? Light.Red : amber ? Light.Amber : Light.Green, string.Join(" · ", parts));
    }

    private static HealthCategory Network(HealthFacts f) => f.NetMbps < 0
        ? new("network", Light.Grey, "network traffic not measured")
        : new("network", f.NetMbps >= NetAmberMbps ? Light.Amber : Light.Green, Words($"{f.NetMbps:0.0} Mbps"));

    private static HealthCategory Sync(HealthFacts f)
    {
        if (f.FreewheelS > 0)
            return new("sync", Light.Amber, Words($"free-wheeling for {f.FreewheelS:0} s: the leader is quiet"));
        return f.SyncRole switch
        {
            "leader" => new("sync", Light.Green, "leads the sync group"),
            "follower" when f.SyncUncertaintyMs < 0 => new("sync", Light.Grey, "following; the clock is not estimated yet"),
            "follower" => new("sync", f.SyncUncertaintyMs > SyncRedMs ? Light.Red : f.SyncUncertaintyMs > SyncAmberMs ? Light.Amber : Light.Green,
                Words($"following, ±{f.SyncUncertaintyMs:0.00} ms")),
            _ => new("sync", Light.Grey, "not in a sync group"),
        };
    }

    private static HealthCategory Devices(HealthFacts f)
    {
        if (f.DevicesTotal <= 0)
            return new("devices", Light.Grey, "no projectors linked");
        if (f.DevicesRed > 0)
            return new("devices", Light.Red, Words($"{f.DevicesRed} of {f.DevicesTotal} projectors red"));
        if (f.DevicesAmber > 0)
            return new("devices", Light.Amber, Words($"{f.DevicesAmber} of {f.DevicesTotal} projectors amber"));
        return f.DevicesGreen > 0
            ? new("devices", Light.Green, Words($"{f.DevicesGreen} projector{Plural(f.DevicesGreen)} green"))
            : new("devices", Light.Grey, Words($"{f.DevicesTotal} projector{Plural(f.DevicesTotal)} not heard yet"));
    }

    private static HealthCategory Media(HealthFacts f)
    {
        if (f.MediaItems <= 0)
            return new("media", Light.Grey, "no media files");
        return f.MediaMissing > 0
            ? new("media", Light.Amber, Words($"{f.MediaMissing} of {f.MediaItems} files missing"))
            : new("media", Light.Green, Words($"{f.MediaItems} file{Plural(f.MediaItems)} ready"));
    }

    private static HealthCategory Watchdog(HealthFacts f)
    {
        if (f.LaneMs < 0 && f.RenderMs < 0)
            return new("watchdog", Light.Grey, "the lane and the render are not timed on this node");
        var red = f.LaneMs >= LaneRedMs || f.RenderMs >= RenderRedMs;
        var amber = f.LaneMs >= LaneAmberMs || f.RenderMs >= RenderAmberMs;
        var parts = new List<string>();
        if (f.LaneMs >= 0)
            parts.Add(Words($"lane {f.LaneMs:0} ms"));
        if (f.RenderMs >= 0)
            parts.Add(Words($"frame p95 {f.RenderMs:0.0} ms"));
        return new("watchdog", red ? Light.Red : amber ? Light.Amber : Light.Green, string.Join(" · ", parts));
    }

    /// <summary>"FOH-A · 1 amber: memory 1.2 GB free" or "FOH-A · all green · 3 unknown": facts, and no number that moves while all is
    /// well (so a board does not change while nothing changed).</summary>
    public static string Headline(HealthFacts f, ImmutableArray<HealthCategory> categories, Light light)
    {
        ArgumentNullException.ThrowIfNull(f);
        var role = f.Role is "solo" or "" ? "" : " · " + f.Role;
        if (light is Light.Red or Light.Amber)
        {
            var problems = categories.Where(c => c.Light == light).ToList();
            return Words($"{f.Name}{role} · {problems.Count} {ShowContext.LightWord(light)}: {problems[0].Name} — {problems[0].Words}");
        }
        var unknown = categories.Count(c => c.Light == Light.Grey);
        if (light == Light.Grey)
            return $"{f.Name}{role} · nothing measured yet";
        return unknown > 0 ? Words($"{f.Name}{role} · all green · {unknown} unknown") : $"{f.Name}{role} · all green";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>HEALTH's JSON (SPINE §10.3): the facts in the contract's field names, each category's light and words, the light and the
/// headline. Parsed back bounded (≤ 64 KB, ≤ 64 outputs, ≤ 16 categories); a field missing reads unknown.</summary>
public static class HealthJson
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxOutputs = 64;

    public static string Write(HealthRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var w = new Utf8JsonWriter(buffer))
            Write(w, record);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void Write(Utf8JsonWriter w, HealthRecord record)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(record);
        var f = record.Facts;
        w.WriteStartObject();
        w.WriteNumber("v", 1);
        w.WriteString("node", f.Node);
        w.WriteString("name", f.Name);
        w.WriteNumber("at", f.AtUnixMs);
        w.WriteString("role", f.Role);
        w.WriteString("of", f.Of);
        w.WriteNumber("epoch", f.Epoch);
        w.WriteString("build", f.Build);
        w.WriteString("light", ShowContext.LightWord(record.Light));
        w.WriteString("headline", record.Headline);
        w.WriteStartArray("outputs");
        foreach (var o in f.Outputs)
        {
            w.WriteStartObject();
            w.WriteString("id", o.Id);
            w.WriteString("name", o.Name);
            w.WriteString("light", ShowContext.LightWord(record.LightOf(o)));
            w.WriteBoolean("open", o.Open);
            w.WriteNumber("fps", Round(o.Fps, 2));
            w.WriteNumber("dropped", o.Dropped);
            w.WriteNumber("missed60s", o.Missed60s);
            w.WriteString("path", o.Path);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("projectors");
        foreach (var p in f.Projectors)
        {
            w.WriteStartObject();
            w.WriteString("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteString("light", ShowContext.LightWord(p.Light));
            w.WriteString("words", p.Words);
            w.WriteString("output", p.Output);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("render");
        w.WriteString("renderer", f.Renderer);
        w.WriteString("path", f.RenderPath);
        w.WriteNumber("resets", f.Resets);
        w.WriteEndObject();
        w.WriteNumber("cpuPct", Round(f.CpuPct, 1));
        w.WriteNumber("memMb", Round(f.MemMb, 0));
        w.WriteNumber("memFreeMb", Round(f.MemFreeMb, 0));
        w.WriteNumber("diskFreeGb", Round(f.DiskFreeGb, 1));
        w.WriteNumber("gpuPct", Round(f.GpuPct, 1));
        w.WriteNumber("gpuTempC", Round(f.GpuTempC, 1));
        w.WriteNumber("netMbps", Round(f.NetMbps, 1));
        w.WriteNumber("uptimeS", Round(f.UptimeS, 0));
        w.WriteStartObject("sync");
        w.WriteString("role", f.SyncRole);
        w.WriteString("leader", f.SyncLeader);
        w.WriteNumber("uncertaintyMs", Round(f.SyncUncertaintyMs, 3));
        w.WriteNumber("freewheelS", Round(f.FreewheelS, 1));
        w.WriteEndObject();
        w.WriteStartObject("devices");
        w.WriteNumber("total", f.DevicesTotal);
        w.WriteNumber("green", f.DevicesGreen);
        w.WriteNumber("amber", f.DevicesAmber);
        w.WriteNumber("red", f.DevicesRed);
        w.WriteEndObject();
        w.WriteStartObject("media");
        w.WriteNumber("items", f.MediaItems);
        w.WriteNumber("missing", f.MediaMissing);
        w.WriteEndObject();
        w.WriteStartObject("watchdog");
        w.WriteNumber("laneMs", Round(f.LaneMs, 1));
        w.WriteNumber("renderMs", Round(f.RenderMs, 1));
        w.WriteEndObject();
        w.WriteNumber("rev", f.Rev);
        w.WriteStartArray("categories");
        foreach (var c in record.Categories)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("light", ShowContext.LightWord(c.Light));
            w.WriteString("words", c.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>A record another node sent, or null when it is not one (too big, not an object, no node id). The lights are read as
    /// sent (they are that node's own evidence); a light word that is not one reads grey.</summary>
    public static HealthRecord? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxBytes)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "node") is not { Length: > 0 and <= 64 } node)
                return null;
            var outputs = ImmutableArray.CreateBuilder<HealthOutput>();
            if (r.TryGetProperty("outputs", out var os) && os.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in os.EnumerateArray().Take(MaxOutputs))
                {
                    if (o.ValueKind != JsonValueKind.Object)
                        continue;
                    outputs.Add(new HealthOutput(Str(o, "id"), Str(o, "name"), Bool(o, "open", true), Num(o, "fps"), (long)Num(o, "dropped", 0),
                        (long)Num(o, "missed60s", 0), Str(o, "path")));
                }
            }
            var projectors = ImmutableArray.CreateBuilder<HealthProjector>();
            if (r.TryGetProperty("projectors", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ps.EnumerateArray().Take(MaxOutputs))
                {
                    if (p.ValueKind == JsonValueKind.Object)
                        projectors.Add(new HealthProjector(Str(p, "id"), Str(p, "name"), ShowContext.ParseLight(Str(p, "light")), Str(p, "words"), Str(p, "output")));
                }
            }
            var render = Obj(r, "render");
            var sync = Obj(r, "sync");
            var devices = Obj(r, "devices");
            var media = Obj(r, "media");
            var watchdog = Obj(r, "watchdog");
            var facts = new HealthFacts
            {
                Node = node,
                Name = Str(r, "name"),
                AtUnixMs = (long)Num(r, "at", 0),
                Role = Str(r, "role", "solo"),
                Of = Str(r, "of"),
                Epoch = (long)Num(r, "epoch", 0),
                Build = Str(r, "build"),
                Rev = (long)Num(r, "rev"),
                Outputs = outputs.ToImmutable(),
                Projectors = projectors.ToImmutable(),
                Renderer = render is { } re ? Str(re, "renderer") : "",
                RenderPath = render is { } rp ? Str(rp, "path") : "",
                Resets = render is { } rs ? (long)Num(rs, "resets", 0) : 0,
                CpuPct = Num(r, "cpuPct"),
                MemMb = Num(r, "memMb"),
                MemFreeMb = Num(r, "memFreeMb"),
                DiskFreeGb = Num(r, "diskFreeGb"),
                GpuPct = Num(r, "gpuPct"),
                GpuTempC = Num(r, "gpuTempC"),
                NetMbps = Num(r, "netMbps"),
                UptimeS = Num(r, "uptimeS"),
                SyncRole = sync is { } sr ? Str(sr, "role", "solo") : "solo",
                SyncLeader = sync is { } sl ? Str(sl, "leader") : "",
                SyncUncertaintyMs = sync is { } su ? Num(su, "uncertaintyMs") : -1,
                FreewheelS = sync is { } sf ? Num(sf, "freewheelS", 0) : 0,
                DevicesTotal = devices is { } dt ? (int)Num(dt, "total", 0) : 0,
                DevicesGreen = devices is { } dg ? (int)Num(dg, "green", 0) : 0,
                DevicesAmber = devices is { } da ? (int)Num(da, "amber", 0) : 0,
                DevicesRed = devices is { } dr ? (int)Num(dr, "red", 0) : 0,
                MediaItems = media is { } mi ? (int)Num(mi, "items", 0) : 0,
                MediaMissing = media is { } mm ? (int)Num(mm, "missing", 0) : 0,
                LaneMs = watchdog is { } wl ? Num(wl, "laneMs") : -1,
                RenderMs = watchdog is { } wr ? Num(wr, "renderMs") : -1,
            };
            var categories = ImmutableArray.CreateBuilder<HealthCategory>();
            if (r.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray().Take(16))
                {
                    if (c.ValueKind == JsonValueKind.Object)
                        categories.Add(new HealthCategory(Str(c, "name"), ShowContext.ParseLight(Str(c, "light")), Str(c, "words")));
                }
            }
            var graded = categories.Count > 0 ? categories.ToImmutable() : HealthLights.Grade(facts);
            var light = r.TryGetProperty("light", out _) ? ShowContext.ParseLight(Str(r, "light")) : HealthLights.Worst(graded.Select(c => c.Light));
            return new HealthRecord(facts, graded, light, Str(r, "headline"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Round(double v, int digits) => v < 0 ? -1 : Math.Round(v, digits);

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string Str(JsonElement e, string name, string fallback = "")
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return fallback;
        var s = v.GetString() ?? fallback;
        return s.Length > 512 ? s[..512] : s;
    }

    private static double Num(JsonElement e, string name, double fallback = -1) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : fallback;

    private static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
