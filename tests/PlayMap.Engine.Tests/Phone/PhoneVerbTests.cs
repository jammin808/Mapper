using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// PHONE INVITE, LIST and FORGET through the engine: the invite goes to the person asking and to nobody else (never an assistant, MCP
/// or an approval), its ticket and the key it buys appear in no STATE, context, journal or project; the door is the certificate
/// the web port really serves; the preflight row turns amber with the line that invites the phone again when its phone is gone;
/// INDEX's scan section lists the phone cameras; a real node made from the standard parts serves the page and answers the verbs.
/// </summary>
public sealed class PhoneVerbTests
{
    private static string TicketOf(JsonElement invite) => new Uri(invite.GetProperty("url").GetString()!).Fragment["#t=".Length..];

    [Fact]
    public async Task AnInviteGoesToItsAskerAloneWithTheLinkTheQrAndTheFingerprint()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var invite = await rig.Ok("PHONE INVITE Stage left phone FOR 5");
        var url = invite.GetProperty("url").GetString()!;
        Assert.StartsWith("https://192.168.1.44:8443/scan/#t=", url, StringComparison.Ordinal);
        Assert.Equal("Stage left phone", invite.GetProperty("name").GetString());
        Assert.Equal("1a2b 3c4d 5e6f 7a8b", invite.GetProperty("fingerprint").GetString());
        var qr = QrMatrix.Encode(url);
        Assert.Equal(qr.Size, invite.GetProperty("qr").GetProperty("size").GetInt32());
        Assert.Equal(qr.ToBase64Bitset(), invite.GetProperty("qr").GetProperty("modules").GetString());
        var expires = invite.GetProperty("expiresUnixMs").GetInt64();
        var now = rig.PhoneTime.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.InRange(expires - now, 4 * 60_000, 5 * 60_000 + 5_000);
        var journalled = rig.Engine.Journal.Recent(20).Single(r => r.Text.StartsWith("PHONE INVITE", StringComparison.Ordinal));
        Assert.StartsWith("PHONE INVITE \"Stage left phone\" → invite issued, expires ", journalled.Text, StringComparison.Ordinal);

        foreach (var (line, words) in new[]
        {
            ("PHONE INVITE FOR 0", "FOR takes the invite's minutes, 1 to 60"),
            ("PHONE INVITE FOR 61", "FOR takes the invite's minutes, 1 to 60"),
            ("PHONE INVITE " + new string('n', 41), "a phone's name is at most 40 characters"),
        })
        {
            var r = await rig.Run(line);
            Assert.False(r.Ok, line);
            Assert.Equal(words, r.Payload);
        }
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("mcp")]
    [InlineData("approval")]
    public async Task AnInviteIsNeverGivenToAnAssistantMcpOrAnApproval(string transport)
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var refused = await rig.Run("PHONE INVITE", new SessionInfo("x1", transport, "127.0.0.1", true, "Somebody", true));
        Assert.False(refused.Ok);
        Assert.StartsWith("PHONE INVITE answers only a person on their own session", refused.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("#t=", refused.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Engine.Journal.Recent(20), r => r.Text.Contains("invite issued", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wire")]
    [InlineData("desk")]
    [InlineData("companion")]
    public async Task AnInviteIsNeverSentOnTheLineWireToAnotherMachine(string transport)
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var refused = await rig.Run("PHONE INVITE", new SessionInfo("w1", transport, "192.168.1.20", false, "Somebody", true));
        Assert.False(refused.Ok);
        Assert.Equal("PHONE INVITE answers on this machine or through the node's web page: the line wire is not encrypted, and the invite's link is a secret",
            refused.Payload);
        Assert.DoesNotContain("#t=", refused.Payload, StringComparison.Ordinal);
        // The web port (its https page) from another machine, and the line wire on this machine, may ask.
        Assert.True((await rig.Run("PHONE INVITE", new SessionInfo("h1", "http", "192.168.1.20", false, "Browser", true))).Ok);
        Assert.True((await rig.Run("PHONE INVITE", new SessionInfo("w2", "wire", "127.0.0.1", true, "Somebody", true))).Ok);
    }

    [Fact]
    public async Task APhoneJoiningWhileTheShowIsArmedIsRefusedWithTheLocksWordsAndItsInviteHoldsForLater()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var ticket = TicketOf(await rig.Ok("PHONE INVITE Pixel 8"));
        await rig.Ok("SHOW ARM");
        await using (var refused = new FakePhone(rig.Display, rig.Camera, locks: true))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.JoinAsync(rig.Host.Port, ticket));
            Assert.Contains("the node could not add this phone's camera", ex.Message, StringComparison.Ordinal);
            Assert.Contains("armed", ex.Message, StringComparison.Ordinal);
        }
        // Attempts are not facts: no camera rig, no phone.
        Assert.Empty(rig.Engine.Store.Project.Stage.Cameras);
        Assert.Empty(rig.State().GetProperty("phones").EnumerateArray());
        await rig.Ok("SHOW DISARM");
        await using var joined = new FakePhone(rig.Display, rig.Camera, locks: true);
        await joined.JoinAsync(rig.Host.Port, ticket);
        Assert.NotNull(rig.Engine.Store.Project.Stage.Cameras.Find(joined.Rig));
        Assert.True(Assert.Single(rig.State().GetProperty("phones").EnumerateArray()).GetProperty("connected").GetBoolean());
    }

    /// <summary>A phone joining over its own socket with a camera of the given raster; its welcome, or the close and its words.</summary>
    private static async Task<(JsonElement? Welcome, string Words)> HelloAsync(PhoneScanRig rig, object credential, int width, int height)
    {
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        ws.Options.AddSubProtocol(PhoneProtocol.SubProtocol);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Host.Port}/scan/ws"), CancellationToken.None);
        var hello = JsonSerializer.SerializeToNode(new
        {
            t = "hello",
            v = 1,
            camera = new { label = "back", width, height, caps = new { exposure = Array.Empty<string>() } },
        })!.AsObject();
        foreach (var p in JsonSerializer.SerializeToNode(credential)!.AsObject())
            hello[p.Key] = p.Value?.DeepClone();
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello.ToJsonString()), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (got.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            return (null, ws.CloseStatusDescription ?? "");
        var welcome = JsonDocument.Parse(buffer.AsMemory(0, got.Count)).RootElement.Clone();
        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        return (welcome, "");
    }

    [Fact]
    public async Task APhoneComingBackWithAnotherCameraWhileTheShowIsArmedIsRefusedAndItsRigStaysTrue()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var (welcome, _) = await HelloAsync(rig, new { ticket = TicketOf(await rig.Ok("PHONE INVITE Pixel 8")) }, 320, 240);
        var key = welcome!.Value.GetProperty("key").GetString()!;
        var rigId = welcome.Value.GetProperty("camera").GetString()!;
        Assert.True(await OpsRig.Eventually(() => !rig.Hub.Phones()[0].Connected));
        await rig.Ok("SHOW ARM");
        // Turned to portrait: the rig would have to forget its optics, which the armed show does not allow — so the phone is refused.
        var (refused, words) = await HelloAsync(rig, new { key }, 240, 320);
        Assert.Null(refused);
        Assert.Contains("the node could not add this phone's camera", words, StringComparison.Ordinal);
        Assert.Contains("armed", words, StringComparison.Ordinal);
        Assert.Equal(320, rig.Engine.Store.Project.Stage.Cameras.Find(rigId)!.Phone!.Width);
        // STATE, the context and preflight say why it is out, in the lock's words; preflight offers no invite (it would be refused too).
        var phone = Assert.Single(rig.State().GetProperty("phones").EnumerateArray());
        Assert.Contains("was refused", phone.GetProperty("words").GetString()!, StringComparison.Ordinal);
        Assert.Contains("armed", phone.GetProperty("words").GetString()!, StringComparison.Ordinal);
        var row = Assert.Single(await new PhonePreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));
        Assert.Equal(("refused", Light.Amber), (row.Value, row.Light));
        Assert.Null(row.Fix);
        // The same camera coming back while armed (only what it can do changed) is admitted: its optics still hold.
        var (same, _) = await HelloAsync(rig, new { key }, 320, 240);
        Assert.NotNull(same);
        await rig.Ok("SHOW DISARM");
        var (again, _) = await HelloAsync(rig, new { key }, 240, 320);
        Assert.NotNull(again);
        var updated = rig.Engine.Store.Project.Stage.Cameras.Find(rigId)!;
        Assert.Equal((240, 320), (updated.Phone!.Width, updated.Phone.Height));
        Assert.Equal((240, 320), (updated.Intrinsics.Width, updated.Intrinsics.Height));
    }

    [Fact]
    public async Task AnInviteUnderARigsNamePairsThatRigAgainOnceItsPhoneWasForgotten()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        string rigId;
        await using (var phone = await rig.JoinAsync())
        {
            rigId = phone.Rig;
            await rig.Ok($"PHONE FORGET {phone.Id}");
        }
        var row = Assert.Single(await new PhonePreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));
        Assert.Equal("PHONE INVITE \"Pixel 8\"", row.Fix);
        var ticket = TicketOf(await rig.Ok("PHONE INVITE Pixel 8"));
        await using var again = new FakePhone(rig.Display, rig.Camera, locks: true);
        await again.JoinAsync(rig.Host.Port, ticket);
        // The same rig, not a second one; its preflight row green again.
        Assert.Equal(rigId, again.Rig);
        Assert.Single(rig.Engine.Store.Project.Stage.Cameras);
        Assert.Equal("connected", Assert.Single(await new PhonePreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None)).Value);
    }

    [Fact]
    public async Task NoTicketOrKeyReachesStateTheContextTheJournalOrTheProject()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var invite = await rig.Ok("PHONE INVITE Pixel 8");
        var ticket = TicketOf(invite);
        // The key the phone is given, read off its socket as the page would.
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        ws.Options.AddSubProtocol(PhoneProtocol.SubProtocol);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Host.Port}/scan/ws"), CancellationToken.None);
        var hello = JsonSerializer.Serialize(new
        {
            t = "hello",
            v = 1,
            ticket,
            camera = new { label = "back", width = 320, height = 240, caps = new { exposure = Array.Empty<string>() } },
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
        using var welcome = JsonDocument.Parse(buffer.AsMemory(0, got.Count));
        var key = welcome.RootElement.GetProperty("key").GetString()!;
        Assert.True(await OpsRig.Eventually(() => rig.Hub.Phones().Any(p => p.Connected)));

        var state = rig.Engine.State().Json;
        var phone = Assert.Single(JsonDocument.Parse(state).RootElement.GetProperty("phones").EnumerateArray());
        Assert.True(phone.GetProperty("connected").GetBoolean());
        Assert.Equal(welcome.RootElement.GetProperty("camera").GetString(), phone.GetProperty("rig").GetString());
        await rig.Ok("PHONE LIST");
        var everywhere = new[]
        {
            ("STATE", state),
            ("CONTEXT", rig.Engine.Context()),
            ("PHONE LIST", (await rig.Run("PHONE LIST")).Payload!),
            ("journal", string.Join("\n", rig.Engine.Journal.Recent(500).Select(r => r.Text + " " + r.Line))),
            ("project", ProjectJson.Serialize(rig.Engine.Store.Project)),
            ("phones.json", await File.ReadAllTextAsync(Path.Combine(rig.Folder, "phones.json"))),
        };
        foreach (var (where, text) in everywhere)
        {
            Assert.False(text.Contains(ticket, StringComparison.Ordinal), $"the ticket is in {where}");
            Assert.False(text.Contains(key, StringComparison.Ordinal), $"the key is in {where}");
        }
        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Closed, ws.State);
        rig.KeepFolder = true;
        await rig.DisposeAsync();
        var file = await File.ReadAllTextAsync(Path.Combine(rig.Folder, "journal.jsonl"));
        rig.DeleteFolder();
        Assert.Contains("invite issued", file, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket, file, StringComparison.Ordinal);
        Assert.DoesNotContain(key, file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgetDisconnectsAPhoneAndIsRefusedForOneItDoesNotKnow()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        Assert.Equal("no such phone ph00000000", (await rig.Run("PHONE FORGET ph00000000")).Payload);
        var forgot = await rig.Ok($"PHONE FORGET {phone.Id}");
        Assert.Equal(phone.Id, forgot.GetProperty("forgotten")[0].GetString());
        Assert.Empty(rig.State().GetProperty("phones").EnumerateArray());
        Assert.Equal("no phone is paired with this node", (await rig.Run("PHONE FORGET ALL")).Payload);
        // The camera rig stays in the project: a scan through it says why it cannot run.
        Assert.NotNull(rig.Engine.Store.Project.Stage.Cameras.Find(phone.Rig));
    }

    [Fact]
    public async Task ThePreflightRowTurnsAmberWithTheInviteWhenThePhoneIsGone()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var check = new PhonePreflight();
        var context = new PreflightContext(rig.Engine, PreflightDepth.Quick, false);
        var none = Assert.Single(await check.RunAsync(context, CancellationToken.None));
        Assert.Equal(Light.Green, none.Light);

        var phone = await rig.JoinAsync();
        var row = Assert.Single(await check.RunAsync(context, CancellationToken.None));
        Assert.Equal((Light.Green, "connected", "scan"), (row.Light, row.Value, row.Section));
        Assert.Null(row.Fix);

        // The fault: the phone goes.
        await phone.DisposeAsync();
        Assert.True(await OpsRig.Eventually(() => !rig.Hub.Phones()[0].Connected));
        row = Assert.Single(await check.RunAsync(context, CancellationToken.None));
        Assert.Equal(Light.Amber, row.Light);
        Assert.Equal("not connected", row.Value);
        Assert.Contains("a scan through it would fail", row.Note, StringComparison.Ordinal);
        Assert.Equal("PHONE INVITE \"Pixel 8\"", row.Fix);
        Assert.True(WireGrammar.TryParse(row.Fix!, out var fix, out _));
        Assert.Equal("PHONE INVITE", VerbCatalog.Match(fix!)!.Value.Spec.Name);
        Assert.Equal("Pixel 8", Assert.Single(fix!.Words.Skip(2)));
    }

    [Fact]
    public async Task IndexListsEachPhoneCameraWithTheKeysThatDriveIt()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "scan");
        Assert.Equal(["SCAN NEXT", "SCAN CANCEL", "PHONE LIST"], section.Hint.Keys.Select(k => k.DownLine));
        Assert.All(section.Hint.Keys, k => Assert.True(WireGrammar.TryParse(k.DownLine, out var l, out _) && VerbCatalog.Match(l!) is not null, k.DownLine));
        await using var phone = await rig.JoinAsync();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        var row = Assert.Single(JsonDocument.Parse(ms.ToArray()).RootElement.EnumerateArray());
        Assert.Equal(phone.Rig, row.GetProperty("id").GetString());
        Assert.Equal("phone", row.GetProperty("kind").GetString());
        Assert.Equal("green", row.GetProperty("light").GetString());
        Assert.Equal("ready", row.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ANodeThatServesNoHttpsHasNoDoorForAPhone()
    {
        await using var rig = await PhoneScanRig.StartAsync(door: (session, ct) => PhoneDoors.FindAsync(0, "", session, ct));
        var r = await rig.Run("PHONE INVITE");
        Assert.False(r.Ok);
        Assert.Equal("the web port is closed, so a phone has nothing to open", r.Payload);
        // The rig's own web port is plain HTTP: the handshake finds no certificate.
        var plain = await PhoneDoors.FindAsync(rig.Host.Port, "127.0.0.1", PhoneScanRig.Desk, CancellationToken.None);
        Assert.Null(plain.Base);
        Assert.Contains("turn on encrypted connections first", plain.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealNodeServesThePageAndInvitesWithTheCertificateItsPortServes()
    {
        Assert.Contains(PhoneExtension.Factory, EngineParts.Standard().Extensions);
        await using var node = await HostRig.StartAsync("Phone node", parts: EngineParts.Bare with { Extensions = [PhoneExtension.Factory] });
        Assert.NotNull(node.Engine.Services.Phones);
        Assert.Contains(node.Engine.Services.PreflightChecks, c => c is PhonePreflight);
        using (var http = node.Http())
        using (var page = await http.GetAsync("/scan/"))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        }
        Assert.Empty(node.State().GetProperty("phones").EnumerateArray());

        var invite = JsonDocument.Parse(await node.OkAsync("PHONE INVITE Pixel 8")).RootElement;
        var url = new Uri(invite.GetProperty("url").GetString()!);
        Assert.Equal("https", url.Scheme);
        Assert.Equal(node.Network.HttpPort, url.Port);
        Assert.False(System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(url.Host.Trim('[', ']'))), url.Host);
        var served = TlsIdentity.LoadOrCreate(node.Home.KeysFolder, "Phone node", DateTimeOffset.UtcNow);
        Assert.Equal(TlsFingerprint.Short(served.Fingerprint), invite.GetProperty("fingerprint").GetString());
        // The invite holds nothing a phone has not yet used: no phone is known until one joins.
        Assert.Empty(node.Engine.Services.Phones!.Phones());
    }
}
