using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.PriorityProbe;

/// <summary>The node a node leg runs: its program (PlayMap.Node's exe, or its dll run by dotnet) and the folder of HAP clips it plays,
/// each a .mov the job made before the legs. Both or neither: one without the other is refused with words.</summary>
internal sealed record NodeGiven(string Program, string Clips)
{
    public static NodeGiven? Of(Args args)
    {
        if (!args.Has("node") && !args.Has("clips"))
            return null;
        var program = Path.GetFullPath(args.Text("node"));
        var clips = Path.GetFullPath(args.Text("clips"));
        if (!File.Exists(program))
            throw new ArgumentException($"--node names {program}, which is not there");
        if (!Directory.Exists(clips) || Directory.GetFiles(clips, "*.mov").Length == 0)
            throw new ArgumentException($"--clips names {clips}, which holds no .mov clip");
        return new NodeGiven(program, clips);
    }
}

/// <summary>PlayMap's own node as a leg's busy process, given its home and its class only through the product's own paths. Its home is a
/// fresh folder of the machine's temporary files, which the node's first start fills with its own defaults (a new node id and pairing
/// token, loopback trusted, the process class high; its keys folder the node's keys), so neither the token nor the keys ever land in
/// the leg's uploaded folder: the home is deleted once the node has stopped, its engine log copied into the leg's folder first. Its
/// command line is the documented one (EngineEntry.Usage): headless, the home, a name, no recovery, and the web and wire ports the
/// machine handed this controller free just before (--http, --wire; a start refused one of them because another process took it in the
/// gap is started once more on two new ports, said in the row); the rest is the node's defaults, one node at a time (it binds every
/// interface and announces itself as a node on a show network does, its sync port the default). Started, it is brought to work over its
/// own line wire: first its class, read from STATE (machine.platform.settings.process) and, where the leg asks another, set by MACHINE
/// SET PROCESS, which the node keeps and claims at once (its answer "applies now" and, on Windows, its claim applied; anything else
/// breaks the leg with the node's words), so the node claims its class itself and the probe never sets it; then each clip imported
/// (MEDIA IMPORT), the project reopened with one output per clip, 64 x 36 and enabled, showing it (a node decodes only what an enabled
/// output draws: RenderEngine skips a disabled output and EngineSources opens a source only for a draw; the smallest output keeps the
/// draw's own share as small as the node allows), and every clip's decoder seen open in STATE. Then its class is read back from the OS and from STATE's claim. A thread of this
/// controller, which claims HIGH by its own hand first on Windows (the measurement's class, never the node's) and stamps at the highest
/// thread priority, reads at the victim's window's start and end the node's processor time, its every thread's name and processor time,
/// and STATE's decode, output and pacing figures. The rows are written once the node has been stopped. A reading, never a pass mark: the
/// leg is broken only when the node did not come up, did not take its class, did not get to work, or read back another class than the
/// leg asked (Windows).</summary>
internal sealed partial class NodeRun : IDisposable
{
    public const string File = "node.csv";
    public const string ThreadsFile = "node.threads.csv";
    public const string NodeName = "Priority probe node";
    public const int OutputWidth = 64;
    public const int OutputHeight = 36;
    public static readonly TimeSpan UpBound = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan WorkBound = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AskBound = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(10);

    /// <summary>The engine's exit code when neither its wire nor its web port could listen (EngineEntry.BindRefused).</summary>
    private const int BindRefusedExit = 3;

    [GeneratedRegex(@"is up: wire (\d+|closed), web (\d+|closed), renderer ([^;\r\n]*);", RegexOptions.CultureInvariant)]
    private static partial Regex UpLine();

    private readonly string _dir;
    private readonly string _home;
    private readonly string _asked;
    private readonly List<string> _clips;
    private readonly List<string> _words = [];
    private Child? _node;
    private LineClient? _wire;
    private Thread? _stamper;
    private int _pid;
    private string _renderer = "";
    private string _controller = "";
    private long _upNs;
    private long _logFrom;
    private long _atWorkNs;
    private long _stopNs;
    private (string Class, string Base) _classAtWork = ("", "");
    private (string Class, string Base) _classAtEnd = ("", "");
    private (string ReadBack, string Applied) _claim = ("", "");
    private (string ReadBack, string Applied) _renderMmcss = ("", "");
    private (string ReadBack, string Applied) _presentMmcss = ("", "");
    private Stamp? _start;
    private Stamp? _end;
    private bool _stopped;

    private NodeRun(string dir, string home, string asked, List<string> clips)
    {
        _dir = dir;
        _home = home;
        _asked = asked;
        _clips = clips;
    }

    /// <summary>The node came up and is at work: every clip imported and its decoder open.</summary>
    public bool Working { get; private set; }

    /// <summary>Starts the node on a fresh home, gives it the class <paramref name="asked"/> by its own verb and brings it to work; what
    /// went wrong goes to <paramref name="problems"/> and leaves <see cref="Working"/> false.</summary>
    public static async Task<NodeRun> StartAsync(string dir, NodeGiven given, string asked, List<string> problems)
    {
        var clips = Directory.GetFiles(given.Clips, "*.mov").Order(StringComparer.Ordinal).ToList();
        var home = Directory.CreateTempSubdirectory("priority-probe-node-").FullName;
        var run = new NodeRun(dir, home, asked, clips);
        try
        {
            run._controller = RaiseController();
            var port = await run.LaunchAsync(given.Program, problems);
            if (port is null)
                return run;
            run._upNs = MonoClock.Instance.NowNs;
            run._wire = LineClient.Connect(port.Value);
            if (run.TakeClass() is { } unclaimed)
            {
                problems.Add("the node did not take its class: " + unclaimed);
                return run;
            }
            if (await run.BringToWorkAsync() is { } refused)
            {
                problems.Add("the node did not get to work: " + refused);
                return run;
            }
            run._atWorkNs = MonoClock.Instance.NowNs;
            run._classAtWork = Os.ReadClassOf(run._pid);
            using (var state = run.State())
            {
                run._claim = ClaimOf(state, "process.priority");
                run._renderMmcss = ClaimOf(state, "thread.render.mmcss");
                run._presentMmcss = ClaimOf(state, "thread.present.mmcss");
            }
            var expected = asked == "high" ? "High" : "Normal";
            if (OperatingSystem.IsWindows() && run._classAtWork.Class != expected)
                problems.Add($"the leg asked the node for process {asked} and Windows reads back {run._classAtWork.Class} (base {run._classAtWork.Base})");
            run.Working = true;
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException or JsonException or TimeoutException
                                      or UnauthorizedAccessException or FormatException or KeyNotFoundException)
        {
            problems.Add($"the node did not get to work: {e.GetType().Name}: {e.Message}");
        }
        catch
        {
            // Any other fault leaves the leg: the node it started is stopped and its home deleted first, so neither outlives the leg.
            run.Dispose();
            throw;
        }
        return run;
    }

    /// <summary>The node started on two ports handed out free just before (<see cref="FreePorts"/>) and waited for until it is up; the
    /// wire's port, or null with the reason in <paramref name="problems"/>. A port taken by another process between the handout and the
    /// node's bind (the node binds only at the end of its start, seconds later) refuses that bind: the engine ends BindRefused, or comes
    /// up with its wire or web closed. Such a start is stopped, its two logs kept in the leg's folder (node.refused.log and
    /// node.engine.refused.log), and the node is started once more on the same home and two new free ports, said in the row's words; a
    /// second refusal breaks the leg. Any other failure breaks it at once.</summary>
    private async Task<int?> LaunchAsync(string program, List<string> problems)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (http, wire) = FreePorts();
            _node = Child.StartProgram(_dir, "node", program,
                ["engine", "--headless", "--home", _home, "--name", NodeName, "--http", I(http), "--wire", I(wire), "--no-recover"]);
            if (_node is null)
            {
                problems.Add("the node did not start (node.log says why)");
                return null;
            }
            _pid = _node.Id;
            var (port, bindRefused) = await WaitUpAsync();
            if (port is not null)
                return port;
            if (!bindRefused)
            {
                problems.Add($"the node was not up within {UpBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s (node.log and node.engine.log say what it did)");
                return null;
            }
            if (attempt == 2)
            {
                problems.Add("the node's start was refused a port twice, each time on two ports handed out free just before (node.refused.log, node.engine.refused.log and node.engine.log say which)");
                return null;
            }
            RetireRefusedStart();
            _words.Add("retried: its first start was refused a port taken after it was handed out free (node.refused.log and node.engine.refused.log say which), so it was started once more on two other free ports");
        }
    }

    /// <summary>Stops a start whose bind was refused (bounded by <see cref="ExitBound"/>), keeps its node.log as node.refused.log and its
    /// engine log as node.engine.refused.log, and marks where the next start's engine lines begin, so its up line is read from them alone.</summary>
    private void RetireRefusedStart()
    {
        if (_node is not null && !_node.StopAndWait(ExitBound))
            _words.Add($"the refused start had not ended {ExitBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s after it was stopped");
        _node?.Dispose();
        _node = null;
        System.IO.File.Move(Path.Combine(_dir, "node.log"), Path.Combine(_dir, "node.refused.log"), overwrite: true);
        var log = Path.Combine(_home, "playmap.engine.log");
        if (System.IO.File.Exists(log))
        {
            System.IO.File.Copy(log, Path.Combine(_dir, "node.engine.refused.log"), overwrite: true);
            _logFrom = new FileInfo(log).Length;
        }
    }

    /// <summary>Two ports the machine hands out free now, for the node's web and wire (its --http and --wire): each listener is let go
    /// before the node starts, so a port another process takes in between refuses the node's bind, which <see cref="LaunchAsync"/>
    /// meets by one more start on two new ports.</summary>
    private static (int Http, int Wire) FreePorts()
    {
        using var http = new TcpListener(IPAddress.Any, 0);
        using var wire = new TcpListener(IPAddress.Any, 0);
        http.Start();
        wire.Start();
        return (((IPEndPoint)http.LocalEndpoint).Port, ((IPEndPoint)wire.LocalEndpoint).Port);
    }

    /// <summary>The leg's class through the node's own verb: STATE's machine.platform.settings.process and the platform's name read first;
    /// where the setting is the class the leg asks (high, the node's default), nothing is set; otherwise MACHINE SET PROCESS, which the
    /// node keeps and claims at once (docs/wire/machine.md: PROCESS applies now, so the node is never restarted for it). On a platform
    /// that claims (windows) the class is taken only when the answer's claim says applied: a claim refused or missing is the node's own
    /// words breaking the leg, never "applied" in the row; on the platform none the class is kept and said not claimed. Null, or the
    /// words of what was refused or answered otherwise.</summary>
    private string? TakeClass()
    {
        string? has = null;
        string? platformName = null;
        using (var state = State())
        {
            if (state is not null && state.RootElement.TryGetProperty("machine", out var machine) && machine.TryGetProperty("platform", out var platform)
                && platform.ValueKind == JsonValueKind.Object)
            {
                if (platform.TryGetProperty("settings", out var settings) && settings.TryGetProperty("process", out var process)
                    && process.ValueKind == JsonValueKind.String)
                    has = process.GetString();
                if (platform.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    platformName = name.GetString();
            }
        }
        if (has is null)
            return "STATE named no machine.platform.settings.process";
        if (platformName is null)
            return "STATE named no machine.platform.name";
        if (has == _asked)
        {
            _words.Add($"process {_asked}: the node's own setting at its start, not set");
            return null;
        }
        var reply = _wire!.Payload("c", "MACHINE SET PROCESS " + _asked.ToUpperInvariant(), out var refused);
        if (refused is not null)
            return "MACHINE SET PROCESS: " + refused;
        var answer = Answer(reply);
        var applies = (string?)answer?["applies"];
        var said = (string?)answer?["words"] ?? "";
        if (applies != "now")
            return $"MACHINE SET PROCESS {_asked.ToUpperInvariant()} answered that it applies {applies ?? "(not said)"}, not now: {said}";
        if (platformName == "none")
        {
            _words.Add($"process {has} to {_asked} by MACHINE SET PROCESS: kept, not claimed on this node ({said})");
            return null;
        }
        var claim = answer?["claim"] as JsonObject;
        var applied = claim?["applied"] is JsonValue a && a.TryGetValue<bool>(out var yes) && yes;
        if (!applied)
            return $"the node kept PROCESS {_asked.ToUpperInvariant()} and {platformName} did not apply it: {(string?)claim?["words"] ?? said}";
        _words.Add($"process {has} to {_asked} by MACHINE SET PROCESS, applied now (read back {(string?)claim?["readBack"] ?? ""}): {said}");
        return null;
    }

    /// <summary>An answer's JSON: the payload itself, or the payload an action result carries as text.</summary>
    private static JsonNode? Answer(string reply)
    {
        var answer = JsonNode.Parse(reply);
        return answer?["payload"] is JsonValue p && p.TryGetValue<string>(out var inner) ? JsonNode.Parse(inner) : answer;
    }

    /// <summary>The wire's port from the node's "is up" line, read from its own log past the lines of a refused start; null when it
    /// ended or the bound ran out first, with BindRefused true where its start was refused a port (it ended BindRefused, or came up with
    /// its wire or web closed).</summary>
    private async Task<(int? Port, bool BindRefused)> WaitUpAsync()
    {
        var log = Path.Combine(_home, "playmap.engine.log");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < UpBound)
        {
            if (System.IO.File.Exists(log))
            {
                try
                {
                    using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // A log shorter than the mark was started again by the engine: all of it is the new start's.
                    stream.Seek(stream.Length >= _logFrom ? _logFrom : 0, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    var m = UpLine().Match(await reader.ReadToEndAsync());
                    if (m.Success)
                    {
                        if (m.Groups[1].Value == "closed" || m.Groups[2].Value == "closed")
                            return (null, true);
                        _renderer = m.Groups[3].Value.Trim();
                        return (int.Parse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture), false);
                    }
                }
                catch (IOException)
                {
                    // Read while the node writes it: read again at the next look.
                }
            }
            if (_node is null || _node.Exited)
                return (null, _node?.ExitCode == BindRefusedExit);
            await Task.Delay(100);
        }
        return (null, false);
    }

    /// <summary>Every clip imported, the project reopened with one enabled output per clip showing it, every decoder seen open; null, or
    /// the words of what was refused or never came.</summary>
    private async Task<string?> BringToWorkAsync()
    {
        var wire = _wire!;
        var ids = new List<string>();
        for (var i = 0; i < _clips.Count; i++)
        {
            var reply = wire.Payload("i" + I(i), "MEDIA IMPORT " + WireGrammar.Quote(_clips[i]) + " clip" + I(i), out var refused);
            if (refused is not null)
                return $"MEDIA IMPORT {Path.GetFileName(_clips[i])}: {refused}";
            // The answer is an action result whose payload carries the new item's id.
            var answer = JsonNode.Parse(reply);
            var id = (string?)answer?["id"] ?? (answer?["payload"] is JsonValue p && p.TryGetValue<string>(out var inner) ? (string?)JsonNode.Parse(inner)?["id"] : null);
            if (string.IsNullOrEmpty(id))
                return $"MEDIA IMPORT {Path.GetFileName(_clips[i])} answered no item id: {reply}";
            ids.Add(id);
        }
        var clock = Stopwatch.StartNew();
        JsonNode? project = null;
        while (clock.Elapsed < WorkBound)
        {
            var text = wire.Payload("g", "PROJECT GET", out var refused);
            if (refused is not null)
                return "PROJECT GET: " + refused;
            project = JsonNode.Parse(text);
            var media = project?["media"]?.AsArray() ?? [];
            if (ids.TrueForAll(id => media.Any(m => (string?)m?["id"] == id && !string.IsNullOrEmpty((string?)m?["info"]?["videoCodec"]))))
                break;
            project = null;
            await Task.Delay(250);
        }
        if (project is null)
            return $"the clips were not imported within {WorkBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";
        var first = project["outputs"]?.AsArray().FirstOrDefault() ?? throw new InvalidOperationException("the node's project names no output to copy");
        var outputs = new JsonArray();
        for (var k = 0; k < ids.Count; k++)
        {
            var o = first.DeepClone().AsObject();
            o["id"] = "op_" + I(k + 1);
            o["name"] = "Output " + I(k + 1);
            o["width"] = OutputWidth;
            o["height"] = OutputHeight;
            o["enabled"] = true;
            o["mode"] = "source";
            o["source"] = new JsonObject { ["kind"] = "media", ["id"] = ids[k] };
            outputs.Add(o);
        }
        project["outputs"] = outputs;
        var path = Path.Combine(_home, "probe.playmap");
        System.IO.File.WriteAllText(path, project.ToJsonString(), new UTF8Encoding(false));
        _ = wire.Payload("o", "PROJECT OPEN " + WireGrammar.Quote(path), out var opened);
        if (opened is not null)
            return "PROJECT OPEN: " + opened;
        clock.Restart();
        while (clock.Elapsed < WorkBound)
        {
            using var state = State();
            if (state is not null && state.RootElement.TryGetProperty("decode", out var decode) && decode.TryGetProperty("sources", out var sources)
                && sources.GetArrayLength() >= ids.Count)
                return null;
            await Task.Delay(250);
        }
        return $"STATE listed fewer than {I(ids.Count)} open decoders within {WorkBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s of PROJECT OPEN";
    }

    /// <summary>STATE as the node answers it now, or null with its words kept when it does not answer.</summary>
    private JsonDocument? State()
    {
        try
        {
            if (_wire is null)
            {
                _words.Add("STATE not answered: no wire");
                return null;
            }
            var text = _wire.Payload("s", "STATE", out var refused);
            if (refused is not null)
            {
                _words.Add("STATE not answered: " + refused);
                return null;
            }
            return JsonDocument.Parse(text);
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException or TimeoutException or InvalidOperationException)
        {
            _words.Add($"STATE not read: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>The claim <paramref name="name"/> (process.priority, thread.render.mmcss, ...) wherever STATE lists the platform's claims:
    /// its read back and whether it applied; "not in STATE" where STATE names no such claim.</summary>
    private static (string ReadBack, string Applied) ClaimOf(JsonDocument? state, string name)
    {
        if (state is null)
            return ("not read", "");
        (string, string)? Find(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("what", out var what) && what.ValueKind == JsonValueKind.String && what.GetString() == name)
                {
                    var read = e.TryGetProperty("readBack", out var r) ? r.GetString() ?? "" : "";
                    var applied = e.TryGetProperty("applied", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? (a.GetBoolean() ? "yes" : "no") : "";
                    return (read, applied);
                }
                foreach (var p in e.EnumerateObject())
                {
                    if (Find(p.Value) is { } hit)
                        return hit;
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    if (Find(item) is { } hit)
                        return hit;
                }
            }
            return null;
        }
        return Find(state.RootElement) ?? ("not in STATE", "");
    }

    /// <summary>Starts the stamping thread for the victim's window [<paramref name="start"/>, <paramref name="end"/>) on the machine's
    /// monotonic clock: at each bound the node's processor time, its threads and STATE's figures, each with the instant it was read.</summary>
    public void StampAt(long start, long end)
    {
        if (!Working || _stamper is not null)
            return;
        _stamper = new Thread(() =>
        {
            using var timer = new RuntimeDeadlineTimer();
            timer.WaitUntil(start);
            _start = Take();
            timer.WaitUntil(end);
            _end = Take();
            _classAtEnd = Os.ReadClassOf(_pid);
        })
        { IsBackground = true, Name = "node stamps", Priority = ThreadPriority.Highest };
        _stamper.Start();
    }

    private Stamp Take()
    {
        var at = MonoClock.Instance.NowNs;
        var cpu = Os.ProcessCpuMs(_pid);
        var threads = Os.Threads(_pid);
        using var state = State();
        return new Stamp(at, cpu, threads, Figures.Of(state), MonoClock.Instance.NowNs);
    }

    /// <summary>Waits for the stamps (bounded), stops the node's tree and waits for it to have ended (bounded by <see cref="ExitBound"/>:
    /// on Windows a stop returns before the process's handles close), then copies its engine log into the leg's folder, writes the node's
    /// row and its threads' rows and deletes its home (tried again over about 2 s while a file in it is still held).</summary>
    public void Stop(List<string> problems)
    {
        if (_stopped)
            return;
        _stopped = true;
        if (_stamper is not null && !_stamper.Join(TimeSpan.FromSeconds(60)))
            problems.Add("the node's stamps did not end within 60 s of the window's end");
        _stopNs = MonoClock.Instance.NowNs;
        _wire?.Dispose();
        _wire = null;
        if (_node is not null && !_node.StopAndWait(ExitBound))
        {
            _words.Add($"the node had not ended {ExitBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s after it was stopped");
            problems.Add($"the node had not ended {ExitBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s after it was stopped");
        }
        _node?.Dispose();
        _node = null;
        try
        {
            var log = Path.Combine(_home, "playmap.engine.log");
            if (System.IO.File.Exists(log))
                System.IO.File.Copy(log, Path.Combine(_dir, "node.engine.log"), overwrite: true);
        }
        catch (IOException e)
        {
            _words.Add("the engine log not copied: " + e.Message);
        }
        if (DeleteHome() is { } kept)
        {
            // The home holds the node's token and keys: one left behind is said in the node's row's words, never uploaded.
            _words.Add("its home not deleted: " + kept);
            System.IO.File.AppendAllText(Path.Combine(_dir, "node.log"), "its home was not deleted: " + kept + Environment.NewLine);
        }
        Write();
        if (!Working)
            return;
        if (_start is null || _end is null)
            problems.Add("the node was not stamped at the window's bounds");
    }

    private void Write()
    {
        static string Claim((string ReadBack, string Applied) c) =>
            c.ReadBack is "not read" or "not in STATE" ? c.ReadBack : $"read back {(c.ReadBack.Length > 0 ? c.ReadBack : "nothing")}, applied {(c.Applied.Length > 0 ? c.Applied : "not said")}";
        var s = _start;
        var e = _end;
        string Cpu(Stamp? x) => x is null || double.IsNaN(x.CpuMs) ? "" : x.CpuMs.ToString("0.0", CultureInfo.InvariantCulture);
        string Mono(Stamp? x) => x is null ? "" : x.At.ToString(CultureInfo.InvariantCulture);
        string Fig(Stamp? x, Func<Figures, string> f) => x?.State is { } st ? f(st) : "";
        Files.WriteCsv(Path.Combine(_dir, File), Rows.NodeHeader,
        [[
            Environment.OSVersion.Platform.ToString(), _pid.ToString(CultureInfo.InvariantCulture), Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            _asked, _classAtWork.Class, _classAtWork.Base, _classAtEnd.Class, _classAtEnd.Base, _claim.ReadBack, _claim.Applied,
            Working ? "yes" : "no", I(_clips.Count), string.Join(';', _clips.Select(Path.GetFileName)),
            $"{I(OutputWidth)}x{I(OutputHeight)}", _renderer,
            _upNs.ToString(CultureInfo.InvariantCulture), _atWorkNs.ToString(CultureInfo.InvariantCulture), _stopNs.ToString(CultureInfo.InvariantCulture),
            Cpu(s), Mono(s), Cpu(e), Mono(e),
            Fig(s, f => f.Fps), Fig(e, f => f.Fps), Fig(s, f => f.OutputDropped), Fig(e, f => f.OutputDropped), Fig(e, f => f.FrameMsP95),
            Fig(s, f => f.MissedVblanks), Fig(e, f => f.MissedVblanks), Fig(s, f => f.DecodeDropped), Fig(e, f => f.DecodeDropped),
            Fig(s, f => f.DecodeLate), Fig(e, f => f.DecodeLate), Fig(e, f => f.DecodeMsP95), Fig(e, f => f.Decoders),
            s is null ? "" : s.StateAt.ToString(CultureInfo.InvariantCulture), e is null ? "" : e.StateAt.ToString(CultureInfo.InvariantCulture),
            _controller, string.Join("; ", _words), Claim(_renderMmcss), Claim(_presentMmcss),
        ]]);
        var rows = new List<string[]>();
        var before = s?.Threads.Where(t => t.Id >= 0).ToDictionary(t => t.Id) ?? [];
        var after = e?.Threads.Where(t => t.Id >= 0).ToDictionary(t => t.Id) ?? [];
        foreach (var id in before.Keys.Union(after.Keys).Order())
        {
            var name = after.TryGetValue(id, out var a) ? a.Name : before[id].Name;
            string Ms(Dictionary<int, ThreadTime> d) => d.TryGetValue(id, out var t) && !double.IsNaN(t.CpuMs) ? t.CpuMs.ToString("0.0", CultureInfo.InvariantCulture) : "";
            rows.Add([id.ToString(CultureInfo.InvariantCulture), name, Group(name), Ms(before), Ms(after)]);
        }
        Files.WriteCsv(Path.Combine(_dir, ThreadsFile), Rows.NodeThreadsHeader, rows);
    }

    /// <summary>A thread's group by its name: Mesa's llvmpipe rasterizer threads, the HAP workers (the second stage), each clip's HAP
    /// decode and read threads, the render thread, the Compute pool, the runtime's pool, and the rest. On Linux a name is cut to 15
    /// characters; on Windows a thread with no description reads "(no name)", which counts with the rest.</summary>
    public static string Group(string name) =>
        name.StartsWith("llvmpipe", StringComparison.Ordinal) ? "mesa llvmpipe"
        : name.StartsWith("hap worker", StringComparison.Ordinal) ? "hap workers"
        : name.StartsWith("hap decode", StringComparison.Ordinal) ? "hap decode"
        : name.StartsWith("hap read", StringComparison.Ordinal) ? "hap read"
        : name == "render" ? "render"
        : name.StartsWith("PlayMap compute", StringComparison.Ordinal) ? "compute pool"
        : name.StartsWith(".NET TP Worker", StringComparison.Ordinal) ? "thread pool"
        : "other";

    /// <summary>On Windows this controller claims HIGH by its own hand and reads it back, so the thread that stamps the node (at the
    /// highest thread priority, 15) is above every thread a HIGH node runs at its own priority (base 13, its AboveNormal threads 14), but
    /// not above a thread MMCSS has raised into the realtime band for its share: the node's render and present threads join MMCSS by
    /// default (machine.mmcss on), so its stamps can still wait on the render thread, and the node's row records STATE's MMCSS claims
    /// for both. A measurement's class, never the node's. Elsewhere nothing is set and the words say so.</summary>
    private static string RaiseController()
    {
        if (!OperatingSystem.IsWindows())
            return "not raised: a priority class is Windows'";
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.High;
            var (cls, bas) = Os.ReadClass();
            return $"the controller claimed High by its own hand; read {cls} (base {bas})";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "the controller's High refused: " + e.Message;
        }
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Lets go of the wire and the node (its tree stopped if it still runs, and waited for within <see cref="ExitBound"/>); a
    /// node never stopped through <see cref="Stop"/> has its home deleted here, so its token and keys never outlive the leg.</summary>
    public void Dispose()
    {
        _wire?.Dispose();
        _wire = null;
        if (_node is not null && !_node.StopAndWait(ExitBound))
            Program.Say($"the node (pid {I(_pid)}) had not ended {ExitBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s after it was stopped");
        _node?.Dispose();
        _node = null;
        if (_stopped)
            return;
        _stopped = true;
        if (DeleteHome() is { } kept)
            Program.Say($"the node's home {_home} was not deleted: {kept}");
    }

    /// <summary>Deletes the node's home, tried up to five times 0.5 s apart while a file in it is still held (a process that has just
    /// ended can hold its files a moment longer on Windows); null once it is gone, else the last refusal's words.</summary>
    private string? DeleteHome()
    {
        string? last = null;
        using var timer = new RuntimeDeadlineTimer();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
                timer.WaitUntil(MonoClock.Instance.NowNs + 500_000_000L);
            try
            {
                if (Directory.Exists(_home))
                    Directory.Delete(_home, recursive: true);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                last = e.Message;
            }
        }
        return last;
    }

    private sealed record Stamp(long At, double CpuMs, List<ThreadTime> Threads, Figures? State, long StateAt);

    /// <summary>STATE's decode, output and pacing figures as cells: the enabled outputs' mean fps, dropped frames and missed vblanks
    /// summed, their worst frame time p95; every open decoder's dropped and late frames summed and its worst decode p95 (each a count
    /// since that decoder or output opened, so a window's own is the end's less the start's). A figure STATE does not carry is an empty
    /// cell, never a zero: missed vblanks only from an enabled output whose present is an object (an output with no present view writes
    /// present: null), each other figure only from the rows that carry it.</summary>
    private sealed record Figures(string Fps, string OutputDropped, string FrameMsP95, string MissedVblanks, string DecodeDropped,
        string DecodeLate, string DecodeMsP95, string Decoders)
    {
        public static Figures? Of(JsonDocument? state)
        {
            if (state is null)
                return null;
            var root = state.RootElement;
            var fps = new List<double>();
            double? dropped = null, missed = null, frame = null;
            if (root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in outputs.EnumerateArray())
                {
                    if (!(o.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True))
                        continue;
                    if (Num(o, "fps") is { } f)
                        fps.Add(f);
                    dropped = Sum(dropped, Num(o, "dropped"));
                    frame = Max(frame, Num(o, "frameMsP95"));
                    if (o.TryGetProperty("present", out var present) && present.ValueKind == JsonValueKind.Object)
                        missed = Sum(missed, Num(present, "missedVblanks"));
                }
            }
            double? decDropped = null, late = null, decodeMs = null;
            var decoders = 0;
            if (root.TryGetProperty("decode", out var decode) && decode.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in sources.EnumerateArray())
                {
                    decoders++;
                    decDropped = Sum(decDropped, Num(d, "dropped"));
                    late = Sum(late, Num(d, "late"));
                    decodeMs = Max(decodeMs, Num(d, "decodeMsP95"));
                }
            }
            static string F(double? v, string f = "0") => v is { } x ? x.ToString(f, CultureInfo.InvariantCulture) : "";
            return new Figures(fps.Count == 0 ? "" : F(fps.Average(), "0.0"), F(dropped), F(frame, "0.0"), F(missed), F(decDropped), F(late),
                F(decodeMs, "0.000"), decoders.ToString(CultureInfo.InvariantCulture));
        }

        private static double? Sum(double? acc, double? v) => v is { } x ? (acc ?? 0) + x : acc;

        private static double? Max(double? acc, double? v) => v is { } x ? Math.Max(acc ?? x, x) : acc;

        private static double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }
}

/// <summary>The node's line wire from this controller: one line out, its answer back by the tag it carries (lines the node pushes in
/// between are passed over), each ask bounded. One ask at a time.</summary>
internal sealed class LineClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    private LineClient(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public static LineClient Connect(int port)
    {
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = (int)NodeRun.AskBound.TotalMilliseconds, SendTimeout = (int)NodeRun.AskBound.TotalMilliseconds };
        tcp.Connect("127.0.0.1", port);
        return new LineClient(tcp);
    }

    /// <summary>The payload of the answer to <paramref name="line"/> under <paramref name="tag"/>: "" with the words in
    /// <paramref name="refused"/> for an ERR, or a throw (IOException, TimeoutException) when no answer came in its bound.</summary>
    public string Payload(string tag, string line, out string? refused)
    {
        lock (_gate)
        {
            refused = null;
            _writer.WriteLine("@" + tag + " " + line);
            var ok = "OK @" + tag;
            var err = "ERR @" + tag;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < NodeRun.AskBound)
            {
                var read = _reader.ReadLine() ?? throw new IOException("the node closed its wire");
                if (read == ok)
                    return "";
                if (read.StartsWith(ok + " ", StringComparison.Ordinal))
                    return read[(ok.Length + 1)..];
                if (read == err || read.StartsWith(err + " ", StringComparison.Ordinal))
                {
                    refused = read.Length > err.Length ? read[(err.Length + 1)..] : "refused";
                    return "";
                }
            }
            throw new TimeoutException($"no answer to '{line.Split(' ')[0]}' within {NodeRun.AskBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _tcp.Dispose();
    }
}
