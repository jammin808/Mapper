using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Phone;
using PlayMap.Engine.Wall;
using PlayMap.Net.Http;
using PlayMap.Net.Phone;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>A stage the camera re-solve measures against: the wall 5 m away as a plane surface, and a calibrated simulated camera 6 m
/// back that sees all three projectors' pictures.</summary>
internal static class CameraStage
{
    public const int CameraWidth = 1920;
    public const int CameraHeight = 1080;

    public static Pose CameraPose => Pose.LookAt(new Vec3d(0.2, 1.4, -1), new Vec3d(0, 1.2, 5), Vec3d.UnitY);

    public static Intrinsics CameraOptics => new(1380, 1380, CameraWidth / 2.0, CameraHeight / 2.0, CameraWidth, CameraHeight);

    /// <summary>The project with the wall surface and the simulated camera "cm_sim" added.</summary>
    public static Project With(Project p) => p with
    {
        Stage = p.Stage with
        {
            Meshes = p.Stage.Meshes.Add(new MeshAsset("me_wall", "Wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty, 4, 2)),
            Surfaces = p.Stage.Surfaces.Add(new Surface("sf_wall", "Wall", "me_wall", new Transform3(new Vec3d(0, 1.2, 5), Vec3d.Zero, new Vec3d(14, 7, 1)), [],
                SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", [])),
            Cameras = p.Stage.Cameras.Add(new CameraRig("cm_sim", "Room camera", CameraSourceKind.Simulated, "", CameraOptics, LensDistortion.None, CameraPose, true)),
        },
    };

    /// <summary>The wall a projector truly lights at a pixel (z = 5 m), seen through a solved projector: p95 and max of the distance
    /// over a 24 × 14 grid of the raster (projector px).</summary>
    public static (double P95, double Max) WallError(Projector solved, SimDevice truth)
    {
        var e = new List<double>();
        for (var y = 0; y < 14; y++)
            for (var x = 0; x < 24; x++)
            {
                var px = new Vec2d((x + 0.5) / 24 * WallRig.W, (y + 0.5) / 14 * WallRig.H);
                var ray = CameraModel.PixelRay(px, truth.Intrinsics, truth.Lens, truth.Pose);
                var world = ray.At((5 - ray.Origin.Z) / ray.Direction.Z);
                var seen = CameraModel.Project(world, solved.Intrinsics, solved.Lens, solved.Pose)!.Value;
                e.Add(Vec2d.Distance(seen, px));
            }
        e.Sort();
        return (e[(int)Math.Ceiling(0.95 * e.Count) - 1], e[^1]);
    }
}

/// <summary>CALIBRATION RESOLVE … CAMERA (calibops M5–M7, minimal): three solved projectors on a wall, P2 knocked 0.8° and 2 cm; a fresh
/// look through the room camera at only the named projectors finds P2 moved and the others unmoved, re-solves P2 alone to within
/// 0.3 px p95 of the truth on the wall, in well under two seconds of patterns; P1's and P3's results are untouched.</summary>
public sealed class ResolveCameraTests(ITestOutputHelper output)
{
    private static async Task<WallRig> ThreeSolvedWithCamera()
    {
        var rig = new WallRig(CameraStage.With(WallRig.ProjectorsWithPins(3, noisePx: 0.1)));
        for (var i = 1; i <= 3; i++)
            await rig.Ok($"PROJECTOR SOLVE pj_test{i}");
        return rig;
    }

    [Fact]
    public async Task OnlyTheKnockedProjectorIsResolvedFromTheCameraAndLandsOnTheTruth()
    {
        await using var rig = await ThreeSolvedWithCamera();
        // The simulated world: every projector where it truly stands, P2 knocked 0.8° about a slanted axis and 2 cm sideways.
        for (var n = 0; n < 3; n++)
            rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector($"pj_test{n + 1}")! with { Pose = WallRig.TruePose(n) }, Vec3d.Zero, Vec3d.Zero);
        var axis = new Vec3d(0.6, 0.8, 0).Normalized() * (0.8 * Math.PI / 180);
        rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector("pj_test2")! with { Pose = WallRig.TruePose(1) }, axis, new Vec3d(0.02, 0, 0));
        var knocked = rig.Engine.SimulatedStage.TruthOf(rig.Project.FindProjector("pj_test2")!);
        var p1 = rig.Project.FindProjector("pj_test1")!;
        var p3 = rig.Project.FindProjector("pj_test3")!;
        var stale = CameraStage.WallError(rig.Project.FindProjector("pj_test2")!, knocked);
        output.WriteLine($"knocked: the old pose is {stale.P95:0.0} px p95 off on the wall");
        Assert.True(stale.P95 > 20);

        var reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test1 pj_test2 pj_test3 CAMERA cm_sim"));
        output.WriteLine(reply.GetProperty("words").GetString());
        var attribution = reply.GetProperty("attribution").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!, a => a);
        foreach (var (id, a) in attribution)
            output.WriteLine($"{id}: {a.GetProperty("verdict").GetString()} {a.GetProperty("matched").GetInt32()}/{a.GetProperty("dots").GetInt32()} " +
                $"shift {a.GetProperty("shiftP50").GetDouble():0.00} px");
        Assert.Equal("unmoved", attribution["pj_test1"].GetProperty("verdict").GetString());
        Assert.Equal("moved", attribution["pj_test2"].GetProperty("verdict").GetString());
        Assert.Equal("unmoved", attribution["pj_test3"].GetProperty("verdict").GetString());
        Assert.True(attribution["pj_test2"].GetProperty("matched").GetInt32() >= 0.9 * attribution["pj_test2"].GetProperty("dots").GetInt32());

        // Only P2 was re-solved; P1 and P3 are the very projectors they were, results and all.
        var rows = reply.GetProperty("projectors");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("pj_test2", rows[0].GetProperty("id").GetString());
        Assert.True(rows[0].GetProperty("applied").GetBoolean(), rows[0].GetProperty("why").GetString());
        Assert.Same(p1, rig.Project.FindProjector("pj_test1"));
        Assert.Same(p3, rig.Project.FindProjector("pj_test3"));

        var after = rig.Project.FindProjector("pj_test2")!;
        var (p95, max) = CameraStage.WallError(after, knocked);
        output.WriteLine($"re-solved: {p95:0.000} px p95, {max:0.000} px max on the wall against the truth");
        Assert.True(p95 <= 0.3, $"{p95} px p95");
        var snap = rig.Project.FindCalibration(after.CalibrationId!)!;
        Assert.Equal(CalibrationSource.Resolve, snap.Source);
        Assert.InRange(snap.VerifiedP95Px, 0.001, 0.5);
        Assert.Equal(after.Pose, snap.Projector!.Pose);

        // Pattern time: frames × the pattern hold; one black, one look per named projector, one check of the mover.
        var frames = reply.GetProperty("frames").GetInt32();
        Assert.Equal(5, frames);
        Assert.True(reply.GetProperty("patternSeconds").GetDouble() <= 2, $"{reply.GetProperty("patternSeconds").GetDouble()} s");

        // STATE: the task ran through and nothing is left moved.
        var cal = rig.State().GetProperty("calibration");
        Assert.Equal("done", cal.GetProperty("task").GetProperty("state").GetString());
        Assert.Equal(0, cal.GetProperty("moved").GetArrayLength());
    }

    [Fact]
    public async Task NothingMovedIsRefusedWithTheWordsAndChangesNothing()
    {
        await using var rig = await ThreeSolvedWithCamera();
        for (var n = 0; n < 3; n++)
            rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector($"pj_test{n + 1}")! with { Pose = WallRig.TruePose(n) }, Vec3d.Zero, Vec3d.Zero);
        var before = rig.Project;
        var words = await rig.Refused("CALIBRATION RESOLVE ALL CAMERA cm_sim");
        Assert.Contains("nothing moved", words, StringComparison.Ordinal);
        Assert.Same(before, rig.Project);
    }

    [Fact]
    public async Task EveryProjectorMovingTogetherIsTheCameraAndNoProjectorIsResolved()
    {
        await using var rig = await ThreeSolvedWithCamera();
        for (var n = 0; n < 3; n++)
            rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector($"pj_test{n + 1}")! with { Pose = WallRig.TruePose(n) }, Vec3d.Zero, Vec3d.Zero);
        // The camera knocked half a degree: every projector's dots move together, the projectors stand still.
        rig.Engine.SimulatedStage.Knock(rig.Project.Stage.Cameras[0], new Vec3d(0, 0.5 * Math.PI / 180, 0), Vec3d.Zero);
        var before = rig.Project;
        var words = await rig.Refused("CALIBRATION RESOLVE ALL CAMERA cm_sim");
        output.WriteLine(words);
        Assert.Contains("the camera moved", words, StringComparison.Ordinal);
        Assert.Same(before, rig.Project);
    }

    [Fact]
    public async Task OneProjectorNamedIsJudgedAgainstAnotherSoAMovedCameraIsNotTakenForIt()
    {
        await using var rig = await ThreeSolvedWithCamera();
        for (var n = 0; n < 3; n++)
            rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector($"pj_test{n + 1}")! with { Pose = WallRig.TruePose(n) }, Vec3d.Zero, Vec3d.Zero);
        rig.Engine.SimulatedStage.Knock(rig.Project.Stage.Cameras[0], new Vec3d(0, 0.5 * Math.PI / 180, 0), Vec3d.Zero);
        var before = rig.Project;
        var words = await rig.Refused("CALIBRATION RESOLVE pj_test2 CAMERA cm_sim");
        output.WriteLine(words);
        Assert.Contains("the camera moved", words, StringComparison.Ordinal);
        Assert.Same(before, rig.Project);
    }

    [Fact]
    public async Task ACameraResolveIsRefusedWithoutACalibratedCameraOrAStage()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(2, noisePx: 0.1));
        Assert.Contains("no camera", await rig.Refused("CALIBRATION RESOLVE pj_test1 CAMERA cm_none"), StringComparison.Ordinal);
        var r = await rig.Engine.EditAsync(WallRig.Desk, "camera", p => p.WithStage(st => st with
        {
            Cameras = st.Cameras.Add(new CameraRig("cm_sim", "Room camera", CameraSourceKind.Simulated, "", CameraStage.CameraOptics, LensDistortion.None,
                CameraStage.CameraPose, false)),
        }));
        Assert.True(r.Changed);
        Assert.Contains("not calibrated", await rig.Refused("CALIBRATION RESOLVE pj_test1 CAMERA cm_sim"), StringComparison.Ordinal);
        r = await rig.Engine.EditAsync(WallRig.Desk, "calibrated", p => p.WithStage(st => st with
        {
            Cameras = st.Cameras.Update("cm_sim", c => c with { Calibrated = true }),
        }));
        Assert.True(r.Changed);
        Assert.Contains("no surface", await rig.Refused("CALIBRATION RESOLVE pj_test1 CAMERA cm_sim"), StringComparison.Ordinal);
        Assert.Contains("CAMERA takes a camera", await rig.Refused("CALIBRATION RESOLVE pj_test1 CAMERA"), StringComparison.Ordinal);
    }
    /// <summary>The wall's outputs as a display: the frame on each output now, each present stamped on the monotonic clock the phone's
    /// pictures are.</summary>
    private sealed class WallGlass : IScanDisplay
    {
        private readonly Lock _gate = new();
        private readonly byte[]?[] _frames = new byte[]?[3];

        public byte[]?[] Frames
        {
            get
            {
                lock (_gate)
                    return [.. _frames];
            }
        }

        public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
        {
            var j = int.Parse(outputId.AsSpan("op_test".Length), System.Globalization.CultureInfo.InvariantCulture) - 1;
            lock (_gate)
                _frames[j] = gray8.Span.IndexOfAnyExcept((byte)0) < 0 ? null : gray8.ToArray();
            return Task.FromResult(MonoClock.Instance.NowNs);
        }

        public void Release()
        {
            lock (_gate)
                Array.Clear(_frames);
        }
    }

    [Fact]
    public async Task APhoneRigOpensThroughTheHubAndReachesTheResolve()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-phoneresolve-").FullName;
        var p = CameraStage.With(WallRig.ProjectorsWithPins(3, noisePx: 0.1));
        p = p.WithStage(st => st with { Projectors = [.. st.Projectors.Select((pj, n) => pj with { Pose = WallRig.TruePose(n) })] });
        var glass = new WallGlass();
        // The camera's patience runs on the hub's clock, held still here: a picture is waited for as the fact it is, never raced.
        var hub = new PhoneHub(new PhoneHubOptions { KeysPath = Path.Combine(folder, "phones.json"), NodeId = "0badc0de", NodeName = "Test node", Time = new ManualTime() });
        var services = EngineServices.ForTests(MonoClock.Instance) with
        {
            ProjectFolder = folder,
            Phones = hub,
            PhoneDoor = (_, _) => Task.FromResult(new PhoneDoor(new Uri("https://192.168.1.44:8443"), "1a2b 3c4d", "")),
            WallDisplay = glass,
        };
        var engine = new EngineCore(services, p);
        engine.AttachPhones(hub);
        var host = new HttpHost(services.Settings, engine, services.Wall, new HttpHostOptions { Port = 0, Modules = [hub.Module], Mcp = false });
        FakePhone? phone = null;
        try
        {
            await host.StartAsync();
            async Task<CommandReply> Run(string line)
            {
                Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
                return await engine.HandleAsync(parsed!, WallRig.Desk, CancellationToken.None);
            }
            foreach (var pj in engine.Store.Project.Stage.Projectors)
                engine.SimulatedStage.Knock(pj, Vec3d.Zero, Vec3d.Zero);
            engine.SimulatedStage.Knock(engine.Store.Project.FindProjector("pj_test2")!, new Vec3d(0, 16.0 / 2880, 0), Vec3d.Zero);

            // The phone sees the wall as the stage simulator draws it, lit by what the outputs show, the projectors where they truly stand.
            StageSimulator? sim = null;
            long generation = 0;
            CameraFrame Still()
            {
                sim ??= new StageSimulator(StageWorld.Of(engine.Store.Project, engine.Meshes)!, new SimDevice(CameraStage.CameraOptics, LensDistortion.None, CameraStage.CameraPose),
                    [.. engine.Store.Project.Stage.Projectors.Select(engine.SimulatedStage.TruthOf)]);
                var picture = sim.Capture(glass.Frames);
                return new CameraFrame(picture, CameraStage.CameraWidth, CameraStage.CameraHeight, MonoClock.Instance.NowNs, Interlocked.Increment(ref generation));
            }
            var invite = await Run("PHONE INVITE Pixel 8");
            Assert.True(invite.Ok, invite.Payload);
            var ticket = new Uri(WallRig.Json(invite.Payload!).GetProperty("url").GetString()!).Fragment["#t=".Length..];
            phone = new FakePhone(ct => Task.Run(Still, ct), CameraStage.CameraWidth, CameraStage.CameraHeight);
            await phone.JoinAsync(host.Port, ticket);
            var rigId = phone.Rig;
            // The phone stands where the room camera would, placed and calibrated.
            var edit = await engine.EditAsync(WallRig.Desk, "the phone placed", q => q.WithStage(st => st with
            {
                Cameras = st.Cameras.Update(rigId, c => c with { Intrinsics = CameraStage.CameraOptics, Lens = LensDistortion.None, Pose = CameraStage.CameraPose, Calibrated = true }),
            }));
            Assert.True(edit.Changed);

            var r = await Run($"CALIBRATION RESOLVE ALL CAMERA {rigId}");
            Assert.True(r.Ok, r.Payload);
            var reply = WallRig.Json(r.Payload!);
            output.WriteLine(reply.GetProperty("words").GetString());
            var rows = reply.GetProperty("projectors");
            Assert.Equal("pj_test2", Assert.Single(rows.EnumerateArray()).GetProperty("id").GetString());
            Assert.True(rows[0].GetProperty("applied").GetBoolean(), rows[0].GetProperty("why").GetString());
            Assert.True(phone.Stills >= 5, $"{phone.Stills} stills");
        }
        finally
        {
            if (phone is not null)
                await phone.DisposeAsync();
            await host.DisposeAsync();
            await hub.DisposeAsync();
            await engine.DisposeAsync();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held: the temp folder is the OS's to clear.
            }
        }
    }
}
