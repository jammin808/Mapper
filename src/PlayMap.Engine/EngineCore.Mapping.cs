using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The mapping verbs that call the solvers. The calibration and blend solvers land with their
/// modules; until then the verbs refuse with the reason rather than pretending.</summary>
public sealed partial class EngineCore
{
    private CommandReply SolveProjector(List<string> args)
    {
        var pj = Store.Project.FindProjector(args[0]);
        if (pj is null)
            return CommandReply.Fail($"no projector {args[0]}");
        var pins = pj.Pins.Count(x => x.Enabled);
        if (pins < 6)
            return CommandReply.Fail($"{pj.Name} has {pins} pin{(pins == 1 ? "" : "s")} — a solve needs six or more, spread out and not all on one plane");
        return CommandReply.Fail("the pose solver is not on this node yet");
    }

    private CommandReply SolveBlend(List<string> args) =>
        CommandReply.Fail("the 3D blend solver is not on this node yet");

    private CommandReply ApplyScan(ScanRecord scan) =>
        CommandReply.Fail($"applying {scan.Name} needs the scan module, which is not on this node yet");
}
