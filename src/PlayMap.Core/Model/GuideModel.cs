using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>The guides a project can be set up through: a flat screen's blend or stack, a picture on a wall from a viewer's position,
/// a 3D object, a facade, and the first light of a new node.</summary>
public enum GuideKind
{
    Flat,
    Wall,
    Object,
    Facade,
    First,
}

/// <summary>A step of a guide a person marked as judged done, with the hash of the inputs it was judged on: the mark holds while they
/// are unchanged and lapses once they move. Who marked it and when, in Unix milliseconds.</summary>
public sealed record GuideCheck(string Step, string InputsHash, string Who, long CheckedUnixMs);

/// <summary>A guide begun on the project: its kind, its subject (a canvas for FLAT, a viewpoint for WALL, a projector or a pair for
/// OBJECT), who began it and when (Unix milliseconds), and each step a person checked. A guide's progress is the project itself; this
/// record keeps only what the project cannot show (wp3-workflows).</summary>
public sealed record GuideRecord(GuideKind Kind, string Subject, string Who, long BegunUnixMs, ImmutableArray<GuideCheck> Checks);
