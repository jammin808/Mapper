using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Host;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The operations parts in a real headless node, plugged in through the host's own hooks and reached the way
/// peers reach it: a simulated scan started on the wire runs to its record, its files and SCAN APPLY, its camera view
/// fed meanwhile and closed when the node stops; a DEVICE verb on the wire drives the PJLink simulator with a password
/// from the node's own credential store, which no file the node writes repeats; and an MCP assistant's high-risk line
/// waits on the node's list — the assistant cannot approve it — until the desk's APPROVE runs it.</summary>
public sealed class OpsHostTests
{
    private const string Password = "JBMIA-pj1";

    /// <summary>The operations parts with no PJLink socket (tests share the machine) and quick projector timings.</summary>
    internal static OpsPartsOptions Quick(ScanRunnerOptions? scan = null) => new()
    {
        Scan = scan ?? new ScanRunnerOptions(),
        Devices = new DeviceServiceOptions
        {
            PjLinkUdpPort = -1,
            Timings = DriverTimings.Fast,
            Manager = new DeviceManagerOptions { TickMs = 50 },
        },
    };

    /// <summary>What a wire reply answers: its payload, or — for a verb accepted and not yet applied — the payload
    /// inside its receipt.</summary>
    internal static string Answer(string? line, string tag)
    {
        Assert.NotNull(line);
        Assert.True(line.StartsWith("OK @" + tag, StringComparison.Ordinal), line);
        var rest = line.Length > tag.Length + 4 ? line[(tag.Length + 5)..] : "";
        if (!rest.StartsWith('{'))
            return rest;
        using var doc = JsonDocument.Parse(rest);
        return doc.RootElement.TryGetProperty("stage", out var stage) && stage.GetString() == "accepted"
            ? doc.RootElement.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : ""
            : rest;
    }

    private static Project TwoProjectors()
    {
        Projector Make(string id, string name, Vec3d eye) => new(id, name, Intrinsics.FromThrowRatio(256, 160, 1.4), LensDistortion.None,
            Pose.LookAt(eye, new Vec3d(0, 0.2, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0);
        return Project.CreateDefault("Scan node").WithStage(s => s with
        {
            Projectors = [Make("pj00000001", "Left", new Vec3d(0.1, 1.5, 2.3)), Make("pj00000002", "Right", new Vec3d(-0.8, 1.6, 2.4))],
        });
    }

    [Fact]
    public async Task ARealNodeWithTheOperationsPartsScansTheSimulatedCameraEndToEnd()
    {
        // The exe's composition carries the operations parts first (the render side adds no extension); round-2 packages add theirs
        // after them through their own AddParts hooks.
        var standard = EngineParts.Standard();
        Assert.NotNull(standard.ScanBackend);
        Assert.True(standard.Extensions.Count >= 3, $"{standard.Extensions.Count} parts");

        await using var node = await HostRig.StartAsync("Scan node", parts: OpsParts.With(EngineParts.Bare, Quick()));
        var engine = node.Engine;
        var runner = Assert.IsType<ScanRunner>(engine.Services.Scan);
        Assert.NotNull(engine.Services.Devices);
        Assert.Same(engine.Services.Assistant, engine.Services.Proposals);
        Assert.Empty(node.State().GetProperty("faults").EnumerateArray());
        Assert.Equal(0, node.State().GetProperty("ai").GetProperty("proposals").GetInt32());

        var folder = Directory.CreateDirectory(Path.Combine(node.Dir.FullName, "show")).FullName;
        var path = Path.Combine(folder, "show.playmap");
        await File.WriteAllTextAsync(path, ProjectJson.Serialize(TwoProjectors()));
        await using var wire = await node.WireAsync();
        Answer(await wire.AskAsync("@o PROJECT OPEN " + path), "o");
        Answer(await wire.AskAsync("@v VIEW cam SHOW camera:sim 320x240"), "v");

        using (var started = JsonDocument.Parse(Answer(await wire.AskAsync("@s SCAN START sim MODE FAST"), "s")))
        {
            var scanId = started.RootElement.GetProperty("scan").GetString()!;
            // The test is the camera view's viewer: it renews the view as the desk does (every 5 s), since the node lets a view
            // nobody renews go after 15 s, and a scan on a loaded machine takes longer than that.
            var renewed = Stopwatch.StartNew();
            Assert.True(await Eventually.TrueAsync(async () =>
            {
                if (renewed.Elapsed >= OpsRig.DeskRenewsViews)
                {
                    Answer(await wire.AskAsync("@k VIEW cam KEEP"), "k");
                    renewed.Restart();
                }
                return node.State().GetProperty("scan").GetProperty("state").GetString() is "done" or "failed";
            }, 120_000), "the scan never ended");
            // Renewed as the scan ends too, so the apply below runs well inside the lease before the view is read.
            Answer(await wire.AskAsync("@k VIEW cam KEEP"), "k");
            var scan = node.State().GetProperty("scan");
            Assert.True(scan.GetProperty("state").GetString() == "done", scan.GetProperty("message").GetString());
            Assert.Equal(scanId, scan.GetProperty("id").GetString());
            var record = Assert.Single(engine.Store.Project.Stage.Scans);
            Assert.Equal(scanId, record.Id);
            Assert.True(File.Exists(Path.Combine(folder, record.DataPath, "mesh.ply")));
            Assert.NotNull(runner.Find(scanId));

            using (var applied = JsonDocument.Parse(Answer(await wire.AskAsync("@a SCAN APPLY " + scanId), "a")))
                Assert.All(applied.RootElement.GetProperty("projectors").EnumerateArray(), p => Assert.True(p.GetProperty("applied").GetBoolean(), p.ToString()));
            Assert.All(engine.Store.Project.Stage.Projectors, p => Assert.True(p.SolveResidualPx is > 0 and < 1, $"{p.Name}: {p.SolveResidualPx} px"));
            Assert.Contains(engine.Store.Project.Stage.Meshes, m => m.Origin == MeshOrigin.Scanned);
        }

        // The camera view had the scene, then the scan's frames, then the scene again.
        var ring = engine.Previews.RingPath("cam");
        using (var reader = new PreviewRingReader(ring))
        {
            var pixels = new byte[reader.MaxWidth * reader.MaxHeight * 4];
            Assert.True(reader.TryRead(pixels, 0, out var info));
            Assert.True(info.Sequence > 2, $"{info.Sequence} pictures");
            Assert.Equal((320, 240), (info.Width, info.Height));
        }

        // The node stopping stops the runner's views first: their rings go with them.
        await node.StopAsync();
        Assert.False(File.Exists(ring));
    }

    [Fact]
    public async Task ADeviceVerbOnTheWireDrivesThePjLinkSimulatorWithTheNodesOwnCredentials()
    {
        await using var projector = new PjLinkSimulator(Password);
        await using var node = await HostRig.StartAsync("Device node", parts: OpsParts.With(EngineParts.Bare, Quick()));
        await using var wire = await node.WireAsync();

        var id = Answer(await wire.AskAsync($"@d DEVICE ADD Box pjlink 127.0.0.1:{projector.Port}"), "d");
        Assert.StartsWith(Ids.Device, id, StringComparison.Ordinal);
        Assert.StartsWith("ERR @n", await wire.AskAsync($"@n DEVICE POWER {id} ON"), StringComparison.Ordinal);
        Assert.Equal("0", projector.Power);

        Answer(await wire.AskAsync($"@c CREDENTIAL SET {id} {Password}"), "c");
        using (var receipt = JsonDocument.Parse(Answer(await wire.AskAsync($"@p DEVICE POWER {id} ON"), "p")))
            Assert.Contains(receipt.RootElement.GetProperty("stage").GetString(), new[] { "accepted", "observed" });
        Assert.Equal("1", projector.Power);
        Assert.True(File.Exists(Path.Combine(node.Home.KeysFolder, NodeCredentialStore.FileName)));
        Assert.True(await Eventually.TrueAsync(() => node.State().GetProperty("devices").EnumerateArray()
            .Any(d => d.GetProperty("id").GetString() == id && d.GetProperty("power").GetString() == "on")), node.State().GetProperty("devices").ToString());

        // The password rides in no file the node writes: its journal, its log, its recovery record.
        await node.StopAsync();
        foreach (var file in new[] { node.Home.JournalPath, Path.Combine(node.Home.Folder, NodeLogProvider.FileName), node.Home.RecoveryPath }.Where(File.Exists))
            Assert.DoesNotContain(Password, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        Assert.Contains("a secret was set for", await File.ReadAllTextAsync(node.Home.JournalPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnMcpHighRiskLineWaitsOnTheNodesListUntilTheDeskApprovesIt()
    {
        await using var node = await HostRig.StartAsync("MCP node", parts: OpsParts.With(EngineParts.Bare, Quick()));
        using var http = node.Http();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));
        static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
            string.Concat(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));

        var run = await mcp.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "BLACKOUT ON", ["reason"] = "the house asked for black" });
        string id;
        using (var receipt = JsonDocument.Parse(Text(run)))
        {
            Assert.Equal("queued", receipt.RootElement.GetProperty("stage").GetString());
            id = receipt.RootElement.GetProperty("proposal").GetString()!;
        }
        Assert.False(node.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        Assert.Equal(1, node.State().GetProperty("ai").GetProperty("proposals").GetInt32());

        // The assistant cannot approve its own line: APPROVE from MCP is refused, not queued.
        var self = await mcp.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "APPROVE " + id, ["reason"] = "go ahead" });
        Assert.True(self.IsError, Text(self));
        Assert.False(node.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        Assert.Equal(1, node.State().GetProperty("ai").GetProperty("proposals").GetInt32());

        await using var wire = await node.WireAsync();
        using (var listed = JsonDocument.Parse(Answer(await wire.AskAsync("@l PROPOSALS"), "l")))
        {
            var p = Assert.Single(listed.RootElement.EnumerateArray());
            Assert.Equal((id, "BLACKOUT ON", "pending"), (p.GetProperty("id").GetString(), p.GetProperty("line").GetString(), p.GetProperty("state").GetString()));
            Assert.Equal("the house asked for black", p.GetProperty("reason").GetString());
        }
        using (var done = JsonDocument.Parse(Answer(await wire.AskAsync("@y APPROVE " + id), "y")))
            Assert.Equal("done", done.RootElement.GetProperty("state").GetString());
        Assert.True(node.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        Assert.Equal(0, node.State().GetProperty("ai").GetProperty("proposals").GetInt32());
    }
}
