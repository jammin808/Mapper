using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Projection;

using ProjectFile = PlayMap.Core.Model.ProjectJson;

namespace PlayMap.Engine;

/// <summary>The VIEW verb: the preview views the desk (or any paired peer on this machine) asks for. A view's
/// pictures go to its shared-memory ring (<see cref="PreviewViews.RingPath"/>); the render host draws the
/// editor, outputs and projectors, the scan side's feeds draw cameras and scans. Refusals are cheap and say
/// why — the desk asks quietly and draws its own stand-in for a view the node cannot give.</summary>
public sealed partial class EngineCore
{
    private const string ViewSyntax = "VIEW <view> SHOW <what> [<w>x<h>] | VIEW <view> CAMERA {\"intrinsics\":…,\"pose\":…} | VIEW <view> HIDE";

    private PreviewViews? _previews;

    /// <summary>The node's preview views: the render host attaches to it, the scan side registers its feeds.</summary>
    public PreviewViews Previews => LazyInitializer.EnsureInitialized(ref _previews, () => new PreviewViews(Services.Settings.NodeId));

    private CommandReply View(List<string> args, SessionInfo session)
    {
        // Pictures go to this machine's shared memory: a peer elsewhere gains nothing but load from asking.
        if (!session.Paired && !session.IsLoopback)
            return CommandReply.Fail("not paired — previews are for this machine's desk and paired peers");
        if (args.Count < 2)
            return CommandReply.Fail(ViewSyntax);
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
                    : CommandReply.Success(effect: $"{view} shows {request.What} at most {request.Width}×{request.Height}");
            }
            case "CAMERA":
            {
                if (args.Count != 3 || ReadCamera(args[2]) is not { } camera)
                    return CommandReply.Fail("VIEW <view> CAMERA {\"intrinsics\":…,\"pose\":…} — the eye in the project's own JSON");
                return Previews.Camera(view, camera) is { } notMoved ? CommandReply.Fail(notMoved) : CommandReply.Success(effect: $"{view} looks from its new eye");
            }
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
