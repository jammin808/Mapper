using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Fleet;

/// <summary>What a backup pulls besides media, pure: the files a show names beside its folder (a mesh read from a file, an output's
/// measured warp and blend, a survey's cloud — never a primitive, a path outside the folder or the same path twice), how each is
/// checked by the hash the show names, and what each fleet role may send beside its outputs.</summary>
public sealed class ShowFilesTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void AShowNamesItsMeshesMeasuredMapsAndCloudsBesideIt()
    {
        var p = Project.CreateDefault("Files");
        MeshAsset Mesh(string id, MeshOrigin origin, string path) => new(id, id, origin, path, "sha256:" + Sha, origin == MeshOrigin.Primitive ? "box" : "", ParamSet.Empty, 8, 12);
        var survey = new SurveySource("sv00000001", "North", SurveyFormat.Ply, "/elsewhere/north.ply", "", "LOCAL", 8, 0, "ms00000001", "clouds/sv00000001.ply",
            0.05, 0, SurveyState.Ready, 1, "");
        var show = p with
        {
            Stage = p.Stage with
            {
                Meshes =
                [
                    Mesh("ms00000001", MeshOrigin.Imported, "meshes/ms00000001-wall.ply"),
                    Mesh("ms00000002", MeshOrigin.Primitive, ""),
                    Mesh("ms00000003", MeshOrigin.Imported, "/absolute/wall.ply"),
                    Mesh("ms00000004", MeshOrigin.Imported, "../outside.ply"),
                    Mesh("ms00000005", MeshOrigin.Scanned, "meshes/ms00000001-wall.ply"),
                ],
                Surveys = [survey],
            },
            Outputs = [p.Outputs[0] with
            {
                Measured = new MeasuredCorrection(new MeasuredWarp(3, 2, "align/r1/warp-op_1.pfm", Sha[..16], 0.4), null,
                    new MeasuredBlend(4, 4, "align/r1/blend-op_1.pfm", Sha[..16]), null, 0.4, "", "", "r1", 1),
            }],
        };
        var files = ShowFiles.Of(show);
        Assert.Equal(
            new[]
            {
                ("mesh/ms00000001", "meshes/ms00000001-wall.ply"), ($"warp/{p.Outputs[0].Id}", "align/r1/warp-op_1.pfm"),
                ($"blend/{p.Outputs[0].Id}", "align/r1/blend-op_1.pfm"), ("cloud/sv00000001", "clouds/sv00000001.ply"),
            },
            files.Select(f => (f.Key, f.Path)).ToArray());
        Assert.Equal("", files[^1].Hash);
        Assert.Empty(ShowFiles.Of(Project.CreateDefault("Nothing beside it")));
    }

    /// <summary>A stack member's measured lattice and colour gains live in the show itself (the output's measured correction), so a
    /// backup has them with the show it mirrors: no file beside it to pull.</summary>
    [Fact]
    public void AStackMembersLatticeAndGainsNameNoFileBesideTheShow()
    {
        var p = Project.CreateDefault("Stack");
        var lattice = WarpLattice.AtRest(3, 3) with { Offsets = [.. Enumerable.Repeat(new Vec2d(0.001, -0.002), 9)] };
        var show = p with
        {
            Outputs = [p.Outputs[0] with
            {
                Measured = new MeasuredCorrection(null, lattice, null, new MeasuredColor(0.97, 1, 0.99, 2.2, 1), 0.03, "sc00000001", "sk00000001", "", 1),
            }],
        };
        Assert.Empty(ShowFiles.Of(show));
        Assert.Equal(lattice.Offsets.ToArray(), ProjectJson.Deserialize(ProjectJson.Serialize(show)).Outputs[0].Measured!.Lattice!.Offsets.ToArray());
    }

    [Theory]
    [InlineData("meshes/wall.ply", true)]
    [InlineData("align/r1/warp.pfm", true)]
    [InlineData("../wall.ply", false)]
    [InlineData("meshes/../../wall.ply", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:wall.ply", false)]
    [InlineData("meshes//wall.ply", false)]
    [InlineData("./wall.ply", false)]
    [InlineData("", false)]
    public void APathBesideTheShowIsRelativeAndInsideIt(string path, bool beside) => Assert.Equal(beside, ShowFiles.Beside(path));

    [Fact]
    public void AFileIsTheOneTheShowNamesByItsWholeDigestOrAMapsShortHash()
    {
        Assert.True(ShowFiles.Matches(Sha, "sha256:" + Sha));
        Assert.True(ShowFiles.Matches(Sha, "sha256:" + Sha.ToUpperInvariant()));
        Assert.True(ShowFiles.Matches(Sha, Sha[..16]));      // a measured map's own hash
        Assert.True(ShowFiles.Matches(Sha, ""));             // a cloud: its path is its identity
        Assert.False(ShowFiles.Matches(Sha, "sha256:" + Sha[..16]));
        Assert.False(ShowFiles.Matches(Sha, "fedcba9876543210"));
        Assert.False(ShowFiles.Matches(Sha, Sha[..8]));       // too short to name a file
        Assert.False(ShowFiles.Matches(Sha[..16], Sha[..16]));
    }

    [Fact]
    public void OnlyTheNodeWithTheShowSendsBesideItsOutputs()
    {
        Assert.Equal(NetworkAir.Live, FleetRoles.AirOf(FleetRoles.Solo));
        Assert.Equal(NetworkAir.Live, FleetRoles.AirOf(FleetRoles.Primary));
        Assert.Equal(NetworkAir.Silent, FleetRoles.AirOf(FleetRoles.Backup));
        Assert.Equal(NetworkAir.Held, FleetRoles.AirOf(FleetRoles.Fenced));
    }
}
