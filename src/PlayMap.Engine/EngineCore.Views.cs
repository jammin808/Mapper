using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Projection;

using ProjectFile = PlayMap.Core.Model.ProjectJson;

namespace PlayMap.Engine;

/// <summary>The VIEW verb: the preview views the desk (or any paired peer on this machine) asks for. A view's
/// pictures go to its shared-memory ring, whose file the SHOW reply names (<c>{"view","ring","seq"}</c>, <c>seq</c> the STATE seq
/// from which STATE shows the view so; STATE <c>machine.views</c> lists every shown view's); the render host draws the
/// editor, outputs and projectors, the scan side's feeds draw cameras and scans. Refusals are cheap and say
/// why — the desk asks quietly and draws its own stand-in for a view the node cannot give.</summary>
public sealed partial class EngineCore
{
    private const string ViewSyntax = "VIEW <view> SHOW <what> [<w>x<h>] | VIEW <view> CAMERA {\"intrinsics\":…,\"pose\":…} | VIEW <view> KEEP | VIEW <view> HIDE";

    /// <summary>How often the lane lets lapsed view leases go.</summary>
    private const long ViewExpiryEveryNs = 1_000_000_000;

    private long _viewsExpiredAtNs;

    private PreviewViews? _previews;

    /// <summary>The node's preview views: the render host attaches to it, the scan side registers its feeds.</summary>
    public PreviewViews Previews => LazyInitializer.EnsureInitialized(ref _previews, MakePreviews);

    /// <summary>The views, their changes moving STATE (<c>machine.views</c> names each shown view's ring).</summary>
    private PreviewViews MakePreviews()
    {
        var previews = new PreviewViews(Services.Settings.NodeId, Services.Mono, Math.Max(1, Services.Wall.UnixMs) * 1000);
        previews.Changed += FactsChanged;
        return previews;
    }

    /// <summary>On the lane's tick: views whose viewer stopped renewing them are hidden (once a second at most).</summary>
    private void ExpireViews()
    {
        var now = Services.Mono.NowNs;
        if (_previews is null || now - _viewsExpiredAtNs < ViewExpiryEveryNs)
            return;
        _viewsExpiredAtNs = now;
        foreach (var view in _previews.Expire())
            Journal.Add("view", $"{view} hidden: not renewed for {PreviewViews.LeaseNs / 1_000_000_000} s (VIEW {view} KEEP renews it)", ok: true);
    }

    private CommandReply View(List<string> args, SessionInfo session)
    {
        // Pictures go to this machine's shared memory: a peer elsewhere gains nothing but load from asking.
        if (!session.Paired && !session.IsLoopback)
            return CommandReply.Fail("not paired — previews are for this machine's desk and paired peers");
        if (args.Count < 2)
            return CommandReply.Fail(ViewSyntax);
        if (args.Skip(2).Any(a => a.Equals("SHARED", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail(VerbCatalog.NotAvailableYet("VIEW … SHARED") + " — a view's picture comes through its shared-memory ring");
        var view = args[0];
        if (!ViewRequest.IsViewId(view))
            return CommandReply.Fail("a view id is letters, digits, '-', '_' and '.', at most 64 of them");
        switch (args[1].ToUpperInvariant())
        {
            case "SHOW":
            {
                if (args.Count is < 3 or > 4)
                    return CommandReply.Fail("VIEW <view> SHOW <what> [<w>x<h>]");
                if (ViewRequest.Parse(view, args[2], args.Count > 3 ? args[3] : null, out var why) is not { } request)
                    return CommandReply.Fail(why);
                switch (request.Kind)
                {
                    case ViewKind.Output:
                        if (ResolveOutput(request.TargetId!) is not { } output)
                            return CommandReply.Fail($"no output {request.TargetId}");
                        request = request with { TargetId = output.Id };
                        break;
                    case ViewKind.Projector:
                        var p = Store.Project;
                        if ((p.FindProjector(request.TargetId) ?? p.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(request.TargetId, StringComparison.OrdinalIgnoreCase))) is not { } projector)
                            return CommandReply.Fail($"no projector {request.TargetId}");
                        request = request with { TargetId = projector.Id };
                        break;
                }
                return Previews.Show(request) is { } notShown
                    ? CommandReply.Fail(notShown)
                    : CommandReply.Success(JsonOut(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("view", view);
                        w.WriteString("ring", Previews.RingOf(view) ?? "");
                        // Read after the view moved: every STATE from this seq on shows it as it stands now, and every STATE
                        // written before it moved counts below it.
                        w.WriteNumber("seq", StateKey());
                        w.WriteEndObject();
                    }), $"{view} shows {request.What} at most {request.Width}×{request.Height}");
            }
            case "CAMERA":
            {
                if (args.Count != 3 || ReadCamera(args[2]) is not { } camera)
                    return CommandReply.Fail("VIEW <view> CAMERA {\"intrinsics\":…,\"pose\":…} — the eye in the project's own JSON");
                return Previews.Camera(view, camera) is { } notMoved ? CommandReply.Fail(notMoved) : CommandReply.Success(effect: $"{view} looks from its new eye");
            }
            case "KEEP":
                return args.Count != 2 ? CommandReply.Fail("VIEW <view> KEEP")
                    : Previews.Keep(view) is { } notKept ? CommandReply.Fail(notKept) : CommandReply.Success(effect: $"{view} kept");
            case "HIDE":
                return args.Count != 2 ? CommandReply.Fail("VIEW <view> HIDE")
                    : Previews.Hide(view) is { } notHidden ? CommandReply.Fail(notHidden) : CommandReply.Success(effect: $"{view} hidden");
            default:
                return CommandReply.Fail(ViewSyntax);
        }
    }

    /// <summary>An editor eye from {"intrinsics":…,"pose":…} (near and far are the renderer's own to choose;
    /// optional "content": false tints surfaces by colour, "selected": [ids] outlines them).</summary>
    private static EditorCamera? ReadCamera(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var k = ProjectFile.DeserializeEntity<Intrinsics>(root.GetProperty("intrinsics").GetRawText());
            var pose = ProjectFile.DeserializeEntity<Pose>(root.GetProperty("pose").GetRawText());
            if (k is null || pose is null || k.Width < 1 || k.Height < 1 || !(k.Fx > 0) || !(k.Fy > 0))
                return null;
            var content = !root.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.False;
            HashSet<string>? selected = null;
            if (root.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.Array)
                selected = s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
            return new EditorCamera(k, pose, ShowContent: content, Selected: selected);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
