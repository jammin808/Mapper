using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlayMap.Assistant;
using PlayMap.Core.Control;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Align;
using PlayMap.Engine.Control;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Fx;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Align;
using PlayMap.Engine.Tests.Control;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A node whose host has begun to stop takes no more lines, from any source. In the window where a line source outlives the
/// part its line would start — align's runner already gone, control's OSC socket and bindings still live — a /playmap line over OSC,
/// a bound OSC button and a line handed to the engine are each refused with the stopping words, journalled, and start no ALIGN RUN;
/// the host's own last work still happens: the recovery record holds the show and the journal is flushed. A line already waiting for
/// the lane when the stop begins is refused the same way; an assistant's line over MCP is refused, not queued for a person; a refusal
/// keeps every secret the node holds out of the journal; the node's own lines (a panic's shutters and effects) still run; a learn
/// armed as the stop begins binds nothing.</summary>
public sealed class HostStopTests
{
    private const string Layout = "flat-screen:tile2x1:o20";

    private static readonly SessionInfo Approval = new("approval:pr1", "approval", "10.0.0.5", false, "Approved on Stagewatch", Paired: true);

    /// <summary>An extension that runs a test's work when the host stops it (its place in the list decides when that is).</summary>
    private sealed class Probe : IEngineExtension
    {
        private EngineCore? _engine;

        public Func<EngineCore, Task>? OnStop { get; set; }

        public Exception? Fault { get; private set; }

        public string Name => "probe";

        public Task StartAsync(EngineCore engine, CancellationToken ct)
        {
            _engine = engine;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_engine is null || OnStop is null)
                return;
            try
            {
                await OnStop(_engine);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The host only logs what an extension throws on its way out: the test reads it here.
                Fault = ex;
            }
        }
    }

    /// <summary>An approval queue that keeps every line handed to it.</summary>
    private sealed class KeptQueue : PlayMap.Net.IProposalSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ValueTask<string?> ProposeAsync(string line, string reason, VerbRisk risk, CancellationToken ct)
        {
            Lines.Enqueue(line);
            return ValueTask.FromResult<string?>("p" + Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static async Task<OscMessage> Receive(Socket socket, string address)
    {
        var buffer = new byte[65536];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var got = await socket.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
            if (OscCodec.TryRead(buffer.AsSpan(0, got), out var packet, out _) && packet is OscMessage m && m.Address == address)
                return m;
        }
        throw new TimeoutException("no " + address);
    }

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement.Clone();

    [Fact]
    public async Task ANodeThatHasBegunToStopRefusesEveryLineWithTheWordsAndStartsNoRun()
    {
        var probe = new Probe();
        var parts = EngineParts.Bare with
        {
            // The exe's order, control before align, with the probe between them: the host stops it after align's runner and before
            // control's socket and bindings.
            Extensions = [setup => new ControlExtension(setup), _ => probe, setup => new AlignExtension(setup.Display)],
        };
        await using var node = await HostRig.StartAsync("Stopping node", parts: parts, settings: s => s with
        {
            Control = s.Control with { OscBind = ControlRig.Loopback, OscPort = 0, OscQueryEnabled = false, MidiEnabled = false },
        });
        var engine = node.Engine;
        var runner = Assert.IsType<AlignRunner>(engine.Services.Align);
        var osc = engine.Services.Control?.OscLocal ?? throw new InvalidOperationException("control's OSC socket did not open: " + engine.Services.Control?.OscProblem);

        var folder = Directory.CreateDirectory(Path.Combine(node.Dir.FullName, "show")).FullName;
        var path = Path.Combine(folder, "show.playmap");
        await File.WriteAllTextAsync(path, ProjectJson.Serialize(AlignRig.Project(2, Layout)));
        await node.OkAsync("PROJECT OPEN " + WireGrammar.Quote(path));
        var group = Json(await node.OkAsync("ALIGN NEW Wall cm_sim ALL")).GetProperty("id").GetString()!;
        var binding = Json(await node.OkAsync($"BIND ADD OSC /go TO ALIGN RUN {group}")).GetProperty("id").GetString()!;

        // While the node runs, the composition's runner takes a run (cancelled here: only that it starts matters).
        var first = Json(await node.OkAsync($"ALIGN RUN {group}")).GetProperty("run").GetString()!;
        await node.OkAsync("ALIGN CANCEL");
        await runner.Running!;
        Assert.Equal(first, runner.View!.Id);

        OscMessage? oscReply = null;
        OscMessage? oscError = null;
        CommandReply? handed = null;
        AlignRunView? viewAtStop = null;
        Task? taskAtStop = null;
        probe.OnStop = async e =>
        {
            Assert.True(e.Stopping);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Parse(ControlRig.Loopback), 0));

            // A line under /playmap, from a sender the node trusts.
            await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/line", OscArg.S($"ALIGN RUN {group}"))), SocketFlags.None, osc);
            oscReply = await Receive(client, "/playmap/reply");
            if (oscReply.Args[0].Text == "ERR")
                oscError = await Receive(client, "/playmap/error");

            // A bound button: its binding fires the line, which waits for the lane like any binding's.
            await client.SendToAsync(OscCodec.Write(OscMessage.Of("/go", OscArg.F(1f))), SocketFlags.None, osc);
            await ControlRig.Until(() => e.Journal.Recent(50).Any(r => r.Who == $"binding:{binding} (OSC /go)"), "the binding's line journalled");

            // A line handed to the engine by a source outside the network host (here an approval, as the Stagewatch sink runs one).
            Assert.True(WireGrammar.TryParse($"ALIGN RUN {group}", out var line, out _));
            handed = await e.HandleAsync(line, Approval, CancellationToken.None);

            viewAtStop = runner.View;
            taskAtStop = runner.Running;
        };

        await node.StopAsync();
        Assert.Null(probe.Fault);

        // No run started: the runner's last run is still the one cancelled while the node ran, and its task is that run's.
        Assert.Equal((first, "cancelled"), (viewAtStop!.Id, viewAtStop.State));
        Assert.True(taskAtStop!.IsCompleted);
        Assert.Equal((first, "cancelled"), (runner.View!.Id, runner.View.State));
        Assert.False(runner.IsRunning);

        // Each line is refused with the stopping words: the OSC sender hears them, the line handed in is answered with them.
        Assert.NotNull(oscReply);
        Assert.Equal(("ERR", EngineCore.StoppingWords), (oscReply.Args[0].Text, oscReply.Args[1].Text));
        Assert.Equal(EngineCore.StoppingWords, oscError!.Args[0].Text);
        Assert.NotNull(handed);
        Assert.False(handed.Ok);
        Assert.Equal(EngineCore.StoppingWords, handed.Payload);

        // The journal on disk (flushed on the way out) has each refusal as a refused row naming who sent it, and the node's last word.
        var rows = (await File.ReadAllLinesAsync(node.Home.JournalPath)).Select(Json).ToList();
        var refused = rows.Where(r => r.GetProperty("kind").GetString() == "refused" && r.GetProperty("text").GetString() == EngineCore.StoppingWords)
            .Select(r => (Who: r.TryGetProperty("who", out var w) ? w.GetString() : null, Line: r.GetProperty("line").GetString())).ToList();
        Assert.Contains(($"binding:{binding} (OSC /go)", $"ALIGN RUN {group}"), refused);
        Assert.Contains(refused, r => r.Who!.StartsWith("OSC ", StringComparison.Ordinal) && r.Line == $"ALIGN RUN {group}");
        Assert.Contains((Approval.ClientName, $"ALIGN RUN {group}"), refused);
        Assert.Contains(rows, r => r.GetProperty("text").GetString()!.EndsWith("stopped; the recovery record holds the show", StringComparison.Ordinal));

        // The recovery record was still written as the node stopped, and holds the show as it was: the project, its group and binding.
        var record = RecoveryRecord.Load(node.Home.RecoveryPath);
        Assert.NotNull(record);
        Assert.Equal(path, record.ProjectPath);
        Assert.Contains(group, record.UnsavedProjectJson, StringComparison.Ordinal);
        Assert.Contains(binding, record.UnsavedProjectJson, StringComparison.Ordinal);
    }

    /// <summary>A learn armed when the stop begins binds nothing: a control heard while the host stops (control's OSC socket still
    /// open) is refused with the stopping words, said as learn's outcome and journalled as its refusal; no binding is added and the
    /// show's revision stands.</summary>
    [Fact]
    public async Task ALearnArmedWhenTheStopBeginsBindsNothing()
    {
        var probe = new Probe();
        // The probe after control: the host stops it while control's socket and learn are still live.
        var parts = EngineParts.Bare with { Extensions = [setup => new ControlExtension(setup), _ => probe] };
        await using var node = await HostRig.StartAsync("Stopping node", parts: parts, settings: s => s with
        {
            Control = s.Control with { OscBind = ControlRig.Loopback, OscPort = 0, OscQueryEnabled = false, MidiEnabled = false },
        });
        var engine = node.Engine;
        var osc = engine.Services.Control?.OscLocal ?? throw new InvalidOperationException("control's OSC socket did not open: " + engine.Services.Control?.OscProblem);
        await node.OkAsync("BIND LEARN FROM OSC BLACKOUT TOGGLE");
        Assert.NotNull(engine.ControlInput.Learning);

        long revisionAtStop = 0, revisionAfter = 0;
        string? learnt = null;
        var bindings = 0;
        probe.OnStop = async e =>
        {
            Assert.True(e.Stopping);
            revisionAtStop = e.Store.Current.Revision;
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Parse(ControlRig.Loopback), 0));
            await client.SendToAsync(OscCodec.Write(OscMessage.Of("/go", OscArg.Bool(true))), SocketFlags.None, osc);
            await ControlRig.Until(() => e.ControlInput.Learnt is not null, "learn's outcome");
            (learnt, revisionAfter, bindings) = (e.ControlInput.Learnt, e.Store.Current.Revision, e.Store.Project.Control.Bindings.Length);
        };

        await node.StopAsync();
        Assert.Null(probe.Fault);
        Assert.Equal((EngineCore.StoppingWords, revisionAtStop, 0), (learnt, revisionAfter, bindings));
        Assert.Null(engine.ControlInput.Learning);
        Assert.Empty(engine.Store.Project.Control.Bindings);
        var rows = (await File.ReadAllLinesAsync(node.Home.JournalPath)).Select(Json).ToList();
        Assert.Contains(rows, r => (r.GetProperty("kind").GetString(), r.GetProperty("text").GetString()) == ("refused", "learn refused: " + EngineCore.StoppingWords));
        Assert.DoesNotContain(rows, r => r.GetProperty("text").GetString()!.StartsWith("learnt:", StringComparison.Ordinal));
    }

    /// <summary>Learn's edit waiting for the lane when the stop begins is refused the same way when its turn comes.</summary>
    [Fact]
    public async Task ALearnsEditWaitingForTheLaneWhenTheStopBeginsBindsNothing()
    {
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show());
        await rig.Ok("BIND LEARN BLACKOUT TOGGLE");
        var revision = rig.Engine.Store.Current.Revision;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = rig.Engine.Lane.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait();
        });
        await entered.Task;

        // Learn hears a press and stops waiting; its edit waits behind the lane's work.
        var heard = rig.Engine.ControlInput.InjectAsync(InputAddress.Midi(MidiFamily.Note, 1, 60), 1, CancellationToken.None);
        await ControlRig.Until(() => rig.Engine.ControlInput.Learning is null, "learn to hear the press");
        Assert.False(heard.IsCompleted);

        rig.Engine.BeginStop();
        release.Set();
        await busy;
        await heard;
        Assert.Equal((EngineCore.StoppingWords, revision), (rig.Engine.ControlInput.Learnt, rig.Engine.Store.Current.Revision));
        Assert.Empty(rig.Engine.Store.Project.Control.Bindings);
        Assert.Contains(rig.Engine.Journal.Recent(20), r => (r.Kind, r.Text) == ("refused", "learn refused: " + EngineCore.StoppingWords));
    }

    [Fact]
    public async Task ALineWaitingForTheLaneWhenTheStopBeginsIsRefusedWithTheWordsAndChangesNothing()
    {
        await using var rig = new ShowRig();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The lane is busy with work taken before the stop; a line that passed the door waits behind it.
        var busy = rig.Engine.Lane.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait();
        });
        await entered.Task;
        var waiting = rig.Run("BLACKOUT ON");
        Assert.False(waiting.IsCompleted);

        rig.Engine.BeginStop();
        release.Set();
        await busy;
        var reply = await waiting;

        // The line is refused with the words when its turn comes, journalled as its verb's refusal, and the air is as it was.
        Assert.False(reply.Ok);
        Assert.Equal(EngineCore.StoppingWords, reply.Payload);
        Assert.False(rig.Engine.Air.Blackout);
        Assert.False(rig.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        Assert.Contains(rig.Engine.Journal.Recent(20), r => (r.Kind, r.Text, r.Who, r.Line) == ("refused", EngineCore.StoppingWords, ShowRig.Desk.ClientName, "BLACKOUT ON"));
    }

    /// <summary>An off-lane line's document edit waits for the lane: when its turn comes after the stop began, it is refused with the
    /// words and the project stands — an edit handed to the lane directly (<see cref="EngineCore.EditAsync"/>) and an off-lane verb's
    /// edit (DEVICE ADD) alike. The node's own edit still applies.</summary>
    [Fact]
    public async Task AnOffLaneEditWaitingForTheLaneWhenTheStopBeginsIsRefusedWithTheWordsAndChangesNothing()
    {
        await using var rig = new ShowRig();
        var engine = rig.Engine;
        var revision = engine.Store.Current.Revision;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = engine.Lane.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        await entered.Task;
        Task<EditResult> edit, own;
        Task<CommandReply> device;
        try
        {
            // Both passed their door before the stop; their edits wait behind the lane's work.
            edit = engine.EditAsync(ShowRig.Desk, "rename", p => p with { Name = "Renamed" });
            device = rig.Run("DEVICE ADD Left pjlink 10.0.0.31");
            own = engine.EditAsync(EngineCore.SystemSession, "node's own", p => p with { Name = "The node's own" });
            Assert.False(edit.IsCompleted || device.IsCompleted || own.IsCompleted);
            engine.BeginStop();
        }
        finally
        {
            // The lane is let go whatever happened above: a failed assertion never leaves it held under the rig's dispose.
            release.Set();
        }
        await busy;
        var refused = await edit;
        var reply = await device;
        await own;

        Assert.Equal((false, EngineCore.StoppingWords), (refused.Changed, refused.Effect));
        Assert.Equal((false, EngineCore.StoppingWords), (reply.Ok, reply.Payload));
        Assert.Empty(engine.Store.Project.Stage.Devices);
        // Only the node's own edit landed.
        Assert.Equal((revision + 1, "The node's own"), (engine.Store.Current.Revision, engine.Store.Project.Name));
        Assert.Contains(engine.Journal.Recent(20), r => (r.Kind, r.Text, r.Line) == ("refused", EngineCore.StoppingWords, "DEVICE ADD Left pjlink 10.0.0.31"));
    }

    /// <summary>The calibration ledger's seam answers an accept or a fallback asked for once the stop began as a refused result with
    /// the words (its contract), never an exception; the node's own still runs.</summary>
    [Fact]
    public async Task TheLedgersAcceptAndFallbackAreRefusedWithTheWordsOnceTheStopBegins()
    {
        await using var rig = new ShowRig();
        var ledger = rig.Engine.CalibrationLedgerService;
        rig.Engine.BeginStop();
        var accept = await ledger.AcceptAsync(ShowRig.Desk, "op_1", CancellationToken.None);
        var fallback = await ledger.FallbackAsync(ShowRig.Desk, "op_1", CancellationToken.None);
        Assert.Equal((false, EngineCore.StoppingWords), (accept.Changed, accept.Effect));
        Assert.Equal((false, EngineCore.StoppingWords), (fallback.Changed, fallback.Effect));
        Assert.NotEqual(EngineCore.StoppingWords, (await ledger.FallbackAsync(EngineCore.SystemSession, "op_1", CancellationToken.None)).Effect);
    }

    [Fact]
    public async Task AnAssistantsHighRiskLineOverMcpIsRefusedWithTheWordsOnceTheStopBeginsNotQueuedForAPerson()
    {
        var queue = new KeptQueue();
        var parts = EngineParts.Bare with
        {
            Extensions =
            [
                setup =>
                {
                    setup.Approvals.Use(queue);
                    return new Probe();
                },
            ],
        };
        await using var node = await HostRig.StartAsync("Stopping node", parts: parts);
        using var http = node.Http();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));

        // The node has begun to stop while its web port is still open: a high-risk line (which waits for a person while "AI may act"
        // is off) is refused with the words, never queued on a list that goes with the node.
        node.Engine.BeginStop();
        var run = await mcp.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "BLACKOUT ON", ["reason"] = "a test asks" });
        var text = string.Concat(run.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.True(run.IsError, text);
        Assert.Equal(EngineCore.StoppingWords, text);
        Assert.Empty(queue.Lines);
        Assert.False(node.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        Assert.Contains(node.Engine.Journal.Recent(20), r => (r.Kind, r.Text, r.Who, r.Line) == ("refused", EngineCore.StoppingWords, "MCP", "BLACKOUT ON"));
    }

    [Fact]
    public async Task ALineRefusedAsTheNodeStopsKeepsEverySecretTheNodeHoldsOutOfTheJournal()
    {
        const string ApiKey = "sk-ant-api03-ZYXWVUTSRQPONMLKJIHGFEDCBA0123456789";
        const string ProjectorSecret = "pj-secret-Hx7Q2";
        await using var projector = new PjLinkSimulator(ProjectorSecret);
        var rig = new OpsRig();
        try
        {
            var id = await rig.Ok($"DEVICE ADD Box pjlink 127.0.0.1:{projector.Port}");
            await rig.Ok($"CREDENTIAL SET {id} {ProjectorSecret}");
            await rig.Ok($"CREDENTIAL SET {NodeCredentialStore.AssistantKey} {ApiKey}");

            // Operations lines, each carrying a secret the node holds, arrive once the node has begun to stop.
            rig.Engine.BeginStop();
            var asked = await rig.Run($"ASK \"is {ApiKey} still the key\"");
            var raw = await rig.Run($"DEVICE RAW {id} \"%1PASS {ProjectorSecret}\"");
            Assert.Equal((false, EngineCore.StoppingWords), (asked.Ok, asked.Payload));
            Assert.Equal((false, EngineCore.StoppingWords), (raw.Ok, raw.Payload));

            // Each is journalled as its verb's refusal, with the secret masked as the operations path masks it.
            var rows = rig.Engine.Journal.Recent(50).Where(r => r.Kind == "refused" && r.Text == EngineCore.StoppingWords).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Line!.StartsWith("ASK ", StringComparison.Ordinal) && r.Line.Contains(SecretRedactor.Mask, StringComparison.Ordinal));
            Assert.Contains(rows, r => r.Line!.StartsWith($"DEVICE RAW {id} ", StringComparison.Ordinal) && r.Line.Contains(SecretRedactor.Mask, StringComparison.Ordinal));
            var journal = string.Join("\n", rig.Engine.Journal.Recent(500).Select(r => $"{r.Text} {r.Line} {r.Who}"));
            await rig.DisposeAsync();
            var file = rig.JournalText();
            foreach (var secret in new[] { ApiKey, ProjectorSecret })
            {
                Assert.DoesNotContain(secret, journal, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, file, StringComparison.Ordinal);
            }
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task APanicsOwnLinesStillRunAsTheNodeStopsSoItsShuttersCloseAndItsEffectsGoOff()
    {
        await using var projector = new PjLinkSimulator();
        var p = ShowRig.OneScreen();
        var project = p with
        {
            Outputs = p.Outputs.Add(p.Outputs[0] with { Id = "op_2", Name = "Right" }),
            Show = p.Show with
            {
                Panic = [new OutputPanicPlan("op_1", PanicOutcome.Shutter, 0, null), new OutputPanicPlan("op_2", PanicOutcome.FxOff, 0, null)],
            },
        };
        var rig = new OpsRig(project: project);
        try
        {
            var dv = await rig.Ok($"DEVICE ADD \"Left box\" pjlink 127.0.0.1:{projector.Port} OUTPUT op_1");
            await rig.Ok($"DEVICE POWER {dv} ON");
            await rig.Ok("FX ADD op_2 fx.invert");

            // The panic lands a moment ahead on the show clock; the node begins to stop before its lines are due.
            await rig.Ok("PANIC AT +1500");
            rig.Engine.BeginStop();
            Assert.Equal(EngineCore.StoppingWords, (await rig.Run("PANIC CLEAR")).Payload);
            JsonElement Lines() => rig.State().GetProperty("air").GetProperty("panic").GetProperty("devices");
            Assert.All(Lines().EnumerateArray(), d => Assert.Equal("scheduled", d.GetProperty("stage").GetString()));

            // At its instant the panic's own lines run as the node: the projector's shutter closes (observed) and every effect is off.
            Assert.True(await OpsRig.Eventually(() => projector.Avmt == "31"), $"AVMT {projector.Avmt}; {Lines()}");
            Assert.True(await OpsRig.Eventually(() => Lines().EnumerateArray().Count(d => d.GetProperty("stage").GetString() is "applied" or "accepted") == 2),
                Lines().ToString());
            var slots = FxBoard.Owners(rig.Engine.Store.Project).SelectMany(o => o.Chain.Slots).ToList();
            Assert.NotEmpty(slots);
            Assert.All(slots, s => Assert.True(s.Bypass));
            Assert.Contains(rig.Engine.Journal.Recent(50), r => r.Who == EngineCore.SystemSession.ClientName && r.Line == $"DEVICE SHUTTER {dv} CLOSE" && r.Ok);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
