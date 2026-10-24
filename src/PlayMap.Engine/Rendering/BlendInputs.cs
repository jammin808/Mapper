using System.Collections.Immutable;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Rendering;

/// <summary>What the automatic 3D blend depends on, as a value two snapshots can be compared by: the stage's
/// surfaces, meshes and projectors (by identity — an edit to any of them makes a new array, an untouched one
/// is shared), its stacks (by members and master) and every output that blends automatically (by the fields the solve reads: its blend
/// settings and the γ its fades are drawn through, which its measured layer gives when the hand gives none). Equal inputs give
/// the same blend, so a solve is never repeated for them and a solve's maps are installed only while the
/// inputs they were solved for are still the project's.</summary>
public sealed class BlendInputs : IEquatable<BlendInputs>
{
    private BlendInputs(Stage stage, ImmutableArray<Output> outputs)
    {
        Stage = stage;
        Outputs = outputs;
    }

    public Stage Stage { get; }

    /// <summary>The outputs the solve blends, in project order.</summary>
    public ImmutableArray<Output> Outputs { get; }

    public bool IsEmpty => Outputs.IsEmpty;

    public static BlendInputs Of(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var outputs = ImmutableArray.CreateBuilder<Output>();
        foreach (var o in project.Outputs)
            if (Blends(o))
                outputs.Add(o);
        return new BlendInputs(project.Stage, outputs.ToImmutable());
    }

    /// <summary>True for an output the automatic 3D blend covers: enabled, in Projector mode, driving a
    /// projector, with automatic blending on.</summary>
    public static bool Blends(Output output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Enabled && output.Mode == OutputMode.Projector && output.ProjectorId is not null && output.Blend.Auto3D;
    }

    /// <summary>Why an output is not blended automatically, in the operator's words (null when it is).</summary>
    public static string? WhyNot(Output output, Project project)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(project);
        if (!output.Enabled)
            return $"{output.Name} is off";
        if (output.Mode != OutputMode.Projector)
            return $"{output.Name} is in {output.Mode} mode — the 3D blend is for outputs in Projector mode";
        if (output.ProjectorId is null || project.FindProjector(output.ProjectorId) is null)
            return $"{output.Name} drives no projector on the stage";
        if (!output.Blend.Auto3D)
            return $"{output.Name} blends by hand (its edges) — turn automatic blending on first";
        return null;
    }

    /// <summary>Solves the blend on the caller's thread (pure maths, no GPU); meshes load as needed.</summary>
    public BlendSolution Solve(StageMeshes meshes, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        return BlendSolver3D.Solve(Stage, Outputs, meshes.For(Stage), BlendSolveOptions.Default, progress, ct);
    }

    public bool Equals(BlendInputs? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (Stage.Surfaces != other.Stage.Surfaces || Stage.Meshes != other.Stage.Meshes || Stage.Projectors != other.Stage.Projectors
            || !SameStacks(Stage.Stacks, other.Stage.Stacks) || Outputs.Length != other.Outputs.Length)
            return false;
        for (var i = 0; i < Outputs.Length; i++)
            if (!SameForBlend(Outputs[i], other.Outputs[i]))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as BlendInputs);

    public override int GetHashCode() => HashCode.Combine(Stage.Surfaces.Length, Stage.Projectors.Length, Outputs.Length);

    /// <summary>The stacks as the blend reads them (members and master; a failover policy changes no blend).</summary>
    private static bool SameStacks(ImmutableArray<StackGroup> a, ImmutableArray<StackGroup> b)
    {
        if (a == b)
            return true;
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i].Id != b[i].Id || a[i].MasterId != b[i].MasterId || !a[i].Members.SequenceEqual(b[i].Members, StringComparer.Ordinal))
                return false;
        return true;
    }

    private static bool SameForBlend(Output a, Output b) =>
        ReferenceEquals(a, b)
        || (a.Id == b.Id && a.Enabled == b.Enabled && a.Mode == b.Mode && a.ProjectorId == b.ProjectorId
            && a.Width == b.Width && a.Height == b.Height && a.Blend == b.Blend && a.DrawnBlendGamma().Equals(b.DrawnBlendGamma()));
}

/// <summary>A 3D blend solved for a set of inputs at a project revision: what BLEND SOLVE answered, and what
/// the render host installs while the project's blend inputs are still these.</summary>
public sealed record BlendSolveResult(BlendInputs Inputs, BlendSolution Solution, long Revision);
