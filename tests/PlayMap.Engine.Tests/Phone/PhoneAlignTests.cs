using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Align;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Align;
using PlayMap.Net.Http;
using PlayMap.Net.Phone;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>An align run through a phone: align's extension opens a Phone rig through the node's phone hub (its OpenCamera hook), the
/// phone answering each still cue with what it sees — here the align simulator's flat screen lit by what the display shows — and the
/// run reaches its staged result; with the phone gone, the run fails with the phone's words.</summary>
public sealed class PhoneAlignTests
{
    private const string Layout = "flat-screen:tile2x1:o20";

    /// <summary>The align simulator as display and camera, its presents stamped on the monotonic clock the phone's pictures are.</summary>
    private sealed class MonoDisplay(SimDisplayCamera seam) : IScanDisplay
    {
        public async Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
        {
            await seam.ShowAsync(outputId, gray8, width, height, ct);
            return MonoClock.Instance.NowNs;
        }

        public void Release() => seam.Release();
    }

    private sealed class Node : IAsyncDisposable
    {
        public required string Folder { get; init; }

        public required PhoneHub Hub { get; init; }

        public required HttpHost Host { get; init; }

        public required EngineCore Engine { get; init; }

        public required AlignExtension Align { get; init; }

        public AlignRunner Runner => (AlignRunner)Engine.Services.Align!;

        public async Task<JsonElement> Ok(string line)
        {
            Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
            var r = await Engine.HandleAsync(parsed!, PhoneScanRig.Desk, CancellationToken.None);
            Assert.True(r.Ok, $"{line} → {r.Payload}");
            return JsonDocument.Parse(r.Payload!).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Hub.DisposeAsync();
            await Align.DisposeAsync();
            await Engine.DisposeAsync();
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held: the temp folder is the OS's to clear.
            }
        }
    }

    private static async Task<Node> StartAsync(SimDisplayCamera seam)
    {
        var folder = Directory.CreateTempSubdirectory("playmap-phonealign-").FullName;
        var hub = new PhoneHub(new PhoneHubOptions { KeysPath = Path.Combine(folder, "phones.json"), NodeId = "0badc0de", NodeName = "Test node" });
        var align = new AlignExtension(new MonoDisplay(seam));
        var services = EngineServices.ForTests(MonoClock.Instance) with
        {
            ProjectFolder = folder,
            Phones = hub,
            PhoneDoor = (_, _) => Task.FromResult(new PhoneDoor(new Uri("https://192.168.1.44:8443"), "1a2b 3c4d", "")),
        };
        services = align.Configure(services);
        var engine = new EngineCore(services, AlignRig.Project(2, Layout));
        engine.AttachPhones(hub);
        await align.StartAsync(engine, CancellationToken.None);
        var host = new HttpHost(services.Settings, engine, services.Wall, new HttpHostOptions { Port = 0, Modules = [hub.Module], Mcp = false });
        await host.StartAsync();
        return new Node { Folder = folder, Hub = hub, Host = host, Engine = engine, Align = align };
    }

    private static async Task<FakePhone> JoinAsync(Node node, SimDisplayCamera seam)
    {
        var invite = await node.Ok("PHONE INVITE Pixel 8");
        var ticket = new Uri(invite.GetProperty("url").GetString()!).Fragment["#t=".Length..];
        var phone = new FakePhone(async ct => (await seam.NextAsync(0, TimeSpan.FromSeconds(5), ct))!, AlignRig.CameraW, AlignRig.CameraH);
        await phone.JoinAsync(node.Host.Port, ticket);
        return phone;
    }

    [Fact]
    public async Task AnAlignRunOpensAPhoneThroughTheHubAndReachesItsResult()
    {
        var seam = new SimDisplayCamera(AlignRig.Truth(2, Layout));
        await using var node = await StartAsync(seam);
        await using var phone = await JoinAsync(node, seam);
        var group = (await node.Ok($"ALIGN NEW Wall {phone.Rig} ALL")).GetProperty("id").GetString()!;
        await node.Ok($"ALIGN RUN {group}");
        await node.Runner.Running!;
        Assert.True(node.Runner.View!.State == "staged", node.Runner.View.Words);
        // Every picture came from the phone, a still at a time, while the patterns went up on the display.
        Assert.True(phone.Stills > 20, $"{phone.Stills} stills");
        Assert.All(seam.Shown, n => Assert.True(n > 10, $"{n} frames shown"));
        Assert.True(seam.Released > 0, "the outputs were handed back to the show");
        Assert.True(Assert.Single(node.Hub.Phones()).Connected);
    }

    [Fact]
    public async Task AnAlignRunThroughAPhoneThatIsGoneFailsWithThePhonesWords()
    {
        var seam = new SimDisplayCamera(AlignRig.Truth(2, Layout));
        await using var node = await StartAsync(seam);
        var phone = await JoinAsync(node, seam);
        var rig = phone.Rig;
        await phone.DisposeAsync();
        Assert.True(await OpsRig.Eventually(() => !node.Hub.Phones()[0].Connected));
        var group = (await node.Ok($"ALIGN NEW Wall {rig} ALL")).GetProperty("id").GetString()!;
        await node.Ok($"ALIGN RUN {group}");
        await node.Runner.Running!;
        Assert.Equal("failed", node.Runner.View!.State);
        Assert.Equal("Pixel 8 did not open: Pixel 8 is not connected — open the invite on the phone (PHONE INVITE makes one)", node.Runner.View.Words);
        Assert.All(seam.Shown, n => Assert.Equal(0, n));
    }

    [Fact]
    public void APhoneRigOnANodeWithoutTheHubOpensNoCamera()
    {
        var rig = new CameraRig("cm00000001", "Pixel 8", CameraSourceKind.Phone, "ph00000001", Intrinsics.FromFieldOfView(320, 240, 68), LensDistortion.None,
            Pose.Identity, false);
        Assert.Null(ScanCameras.Open(null, rig));
    }
}
