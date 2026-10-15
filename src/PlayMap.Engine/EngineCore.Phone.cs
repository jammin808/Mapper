using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Core.Wire;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Scanning;
using PlayMap.Net.Phone;

namespace PlayMap.Engine;

/// <summary>wp2-phonescan's hooks into the engine (EngineCore.Modules.cs): PHONE INVITE, LIST and FORGET, SCAN NEXT; STATE.phones;
/// the phones in the context; INDEX's scan section; a joining phone made a camera rig on the lane.</summary>
public sealed partial class EngineCore
{
    /// <summary>The transports an invite is never given to: its link opens this node's camera page, so it goes only to a person
    /// asking on their own session.</summary>
    private static readonly string[] NoInviteTransports = ["assistant", "mcp", "approval"];

    partial void RoutePhone(VerbRoutes routes)
    {
        routes.OffLane("PHONE INVITE", PhoneInviteAsync);
        routes.Lane("PHONE LIST", _ => CommandReply.Success(PhonesJson()));
        routes.Lane("PHONE FORGET", PhoneForget);
        // An attempt: the wait is ended and the pattern goes up again, unless the camera's answer was already on its way (STATE.scan
        // shows which).
        routes.Lane("SCAN NEXT", _ => Services.Scan is ScanRunner runner && runner.Next()
            ? CommandReply.Success(effect: "asked: the waited pattern goes up again for the camera")
            : CommandReply.Fail("no scan is waiting for a picture"));
        routes.Index(new PhoneIndex(this));
    }

    /// <summary>Joins the phone hub to this engine: a joining phone becomes (or updates) its camera rig, and every phone fact moves
    /// STATE.</summary>
    public void AttachPhones(PhoneHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        hub.Join = PhoneJoinAsync;
        hub.Changed += FactsChanged;
    }

    /// <summary>A phone's camera rig, made on its first join (a Phone rig whose source is the phone's id, with a phone camera's field of
    /// view as its prior) or brought up to date when the phone describes another camera; one undoable edit, none when nothing
    /// changed. Answers the rig's id.</summary>
    private async Task<string> PhoneJoinAsync(PhoneJoined joined, CancellationToken ct)
    {
        string rigId = "";
        var r = await EditAsync(SystemSession, $"phone {joined.Name} joined", p =>
        {
            var existing = p.Stage.Cameras.FirstOrDefault(c => c.SourceKind == CameraSourceKind.Phone && string.Equals(c.Source, joined.PhoneId, StringComparison.Ordinal));
            var prior = Intrinsics.FromFieldOfView(joined.Profile.Width, joined.Profile.Height, 68);
            if (existing is null)
            {
                rigId = p.NewId(Ids.Camera);
                var rig = new CameraRig(rigId, joined.Name, CameraSourceKind.Phone, joined.PhoneId, prior, LensDistortion.None, Pose.Identity, false)
                {
                    Phone = joined.Profile,
                };
                return p.WithStage(s => s with { Cameras = s.Cameras.Add(rig) });
            }
            rigId = existing.Id;
            if (existing.Phone == joined.Profile)
                return p;
            // Another camera or raster: intrinsics solved for the old one no longer hold.
            var updated = SameOptics(existing.Phone, joined.Profile) ? existing with { Phone = joined.Profile }
                : existing with { Phone = joined.Profile, Intrinsics = prior, Lens = LensDistortion.None, Calibrated = false };
            return p.WithStage(s => s with { Cameras = s.Cameras.Update(existing.Id, _ => updated) });
        }).ConfigureAwait(false);
        // Decided by the project after the edit, never by the attempt: a refused edit (the show armed, mapping locked) made no rig,
        // or left one whose optics are for another camera or raster than the phone now has.
        if (rigId.Length == 0 || Store.Project.Stage.Cameras.Find(rigId) is not { } stored || !SameOptics(stored.Phone, joined.Profile))
            throw new InvalidOperationException(r.Effect.Length > 0 ? r.Effect : "the project took no camera for it");
        return rigId;
    }

    /// <summary>Whether a rig's phone profile is for the same camera and raster (its solved optics hold).</summary>
    private static bool SameOptics(PhoneProfile? a, PhoneProfile b) =>
        a is { } x && x.CameraLabel == b.CameraLabel && x.Width == b.Width && x.Height == b.Height;

    private async Task<CommandReply> PhoneInviteAsync(VerbCall call)
    {
        if (Services.Phones is not { } hub || Services.PhoneDoor is not { } door)
            return CommandReply.Fail("this node has no phone hub");
        if (NoInviteTransports.Contains(call.Session.Transport, StringComparer.Ordinal))
            return CommandReply.Fail("PHONE INVITE answers only a person on their own session, never an assistant or an approval: its link opens this node's camera page");
        // The link is a secret: from another machine it goes only over the web port, never on the line wire, which is not encrypted.
        if (!call.Session.IsLoopback && call.Session.Transport is not ("http" or "ws"))
            return CommandReply.Fail("PHONE INVITE answers on this machine or through the node's web page: the line wire is not encrypted, and the invite's link is a secret");
        if (!Services.Settings.Phone.Enabled)
            return CommandReply.Fail("phones are switched off on this node");
        var args = call.Args;
        var minutes = Math.Clamp(Services.Settings.Phone.InviteMinutes, 1, 60);
        var fi = args.FindIndex(a => a.Equals("FOR", StringComparison.OrdinalIgnoreCase));
        if (fi >= 0)
        {
            if (fi + 1 >= args.Count || !int.TryParse(args[fi + 1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes is < 1 or > 60)
                return CommandReply.Fail("FOR takes the invite's minutes, 1 to 60");
            args.RemoveRange(fi, 2);
        }
        var name = string.Join(' ', args).Trim();
        if (name.Length == 0)
            name = "Phone";
        if (name.Length > 40)
            return CommandReply.Fail("a phone's name is at most 40 characters");
        var where = await door(call.Session, call.Ct).ConfigureAwait(false);
        if (where.Base is null)
            return CommandReply.Fail(where.Why);
        // A camera rig of this name whose phone the hub no longer knows (forgotten, or its first join never finished) is paired again:
        // the phone takes that rig's phone id, so no second rig is made.
        var orphan = Store.Project.Stage.Cameras.FirstOrDefault(c => c.SourceKind == CameraSourceKind.Phone
            && string.Equals(c.Name, name, StringComparison.Ordinal) && hub.Find(c.Source) is null);
        var invite = hub.Invite(name, TimeSpan.FromMinutes(minutes), call.Session.ClientName, where.Base, orphan?.Source ?? "", orphan?.Id ?? "");
        var json = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("name", invite.Name);
            w.WriteString("url", invite.Url);
            w.WriteStartObject("qr");
            w.WriteNumber("size", invite.Qr.Size);
            w.WriteString("modules", invite.Qr.ToBase64Bitset());
            w.WriteEndObject();
            w.WriteString("fingerprint", where.Fingerprint);
            w.WriteNumber("expiresUnixMs", invite.Expires.ToUnixTimeMilliseconds());
            w.WriteEndObject();
        });
        // The journal keeps that an invite went out and when it lapses, never its link.
        return CommandReply.Success(json, string.Create(CultureInfo.InvariantCulture,
            $"PHONE INVITE \"{invite.Name}\" → invite issued, expires {invite.Expires:HH:mm} UTC"));
    }

    private CommandReply PhoneForget(VerbCall call)
    {
        if (Services.Phones is not { } hub)
            return CommandReply.Fail("this node has no phone hub");
        if (call.Args.Count != 1)
            return CommandReply.Fail("PHONE FORGET <phone>|ALL");
        var forgotten = hub.Forget(call.Args[0]);
        if (forgotten.Count == 0)
            return CommandReply.Fail(call.Args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "no phone is paired with this node" : $"no such phone {call.Args[0]}");
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("forgotten");
            foreach (var id in forgotten)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }), $"forgot {forgotten.Count} phone{(forgotten.Count == 1 ? "" : "s")}: {string.Join(", ", forgotten)}");
    }

    private string PhonesJson() => JsonOut(w =>
    {
        w.WriteStartObject();
        WritePhones(w);
        w.WriteEndObject();
    });

    partial void StatePhone(Utf8JsonWriter w) => WritePhones(w);

    /// <summary>STATE.phones: every phone this node knows, its camera rig, its link and camera, and its words — never a ticket or a key.</summary>
    private void WritePhones(Utf8JsonWriter w)
    {
        w.WriteStartArray("phones");
        foreach (var f in Services.Phones?.Phones() ?? [])
        {
            w.WriteStartObject();
            w.WriteString("id", f.Id);
            w.WriteString("name", f.Name);
            w.WriteString("rig", f.Rig);
            w.WriteString("platform", f.Platform);
            w.WriteString("model", f.Model);
            w.WriteBoolean("connected", f.Connected);
            w.WriteString("address", f.Address);
            w.WriteString("transport", f.Transport);
            w.WriteStartObject("link");
            w.WriteNumber("rttMs", f.RttMs);
            w.WriteNumber("offsetMs", f.OffsetMs);
            w.WriteNumber("uncertaintyMs", f.UncertaintyMs);
            w.WriteNumber("uploadMBps", f.UploadMBps);
            w.WriteEndObject();
            if (f.Camera is { } c)
            {
                w.WriteStartObject("camera");
                w.WriteString("label", c.Label);
                w.WriteNumber("width", c.Width);
                w.WriteNumber("height", c.Height);
                w.WriteNumber("fps", c.Fps);
                w.WriteString("exposure", c.Exposure);
                w.WriteNumber("exposureMs", c.ExposureMs);
                w.WriteNumber("iso", c.Iso);
                w.WriteString("focus", c.Focus);
                w.WriteString("whiteBalance", c.WhiteBalance);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("camera");
            }
            w.WriteString("motion", f.Motion);
            w.WriteBoolean("visible", f.Visible);
            w.WriteNumber("lastSeenUnixMs", f.LastSeenUnixMs);
            w.WriteString("words", f.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    partial void ContextPhone(List<ContextItem> items, List<ContextLink> links)
    {
        if (Services.Phones is not { } hub)
            return;
        var p = _snapshot.Project.Project;
        foreach (var f in hub.Phones())
        {
            var rig = p.Stage.Cameras.Find(f.Rig);
            if (!f.Connected && rig is null)
                continue;
            var light = f.Refused ? Light.Amber : !f.Connected ? Light.Grey : f.Motion == "moved" ? Light.Amber : Light.Green;
            var state = f.Refused ? f.Words
                : !f.Connected ? $"{f.Name}: not connected — PHONE INVITE makes a new link"
                : f.Motion == "moved" ? $"{f.Name}: moved during the last view"
                : f.Camera?.Exposure == "steady" ? $"{f.Name}: ready, brightness kept steady (no exposure lock from a web page)"
                : $"{f.Name}: ready";
            items.Add(new ContextItem("phone:" + f.Id, "phone", f.Name, light, state));
        }
    }

    partial void ValidatePhone(Project p, object entity, ref string? why)
    {
        // A phone rig needs no check beyond the camera's own: its source is a node-local phone id the hub resolves.
    }
}
