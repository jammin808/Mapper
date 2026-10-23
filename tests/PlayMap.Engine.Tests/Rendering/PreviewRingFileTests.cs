using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>A node's preview rings leave no file behind it: a view still shown when the node stops lets its ring go with the engine,
/// and a node making its views takes away the files of its own earlier rings that no writer or reader holds.</summary>
[Collection("render host")]
public sealed class PreviewRingFileTests
{
    private static string[] FilesOf(string node) => Directory.GetFiles(PreviewRing.DefaultFolder(), $"playmap-{node}-*");

    [Fact]
    public async Task ADraftViewStillShownWhenTheNodeStopsLeavesNoFileBehind()
    {
        var p = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Canvas("cv_main"), 64, 48));
        p = p with { Canvases = p.Canvases.Update("cv_main", c => c with { Background = "#00FF00", Layers = [] }) };
        var eye = JsonSerializer.Serialize(new
        {
            intrinsics = JsonDocument.Parse(ProjectJson.SerializeEntity(Intrinsics.FromFieldOfView(160, 90, 40))).RootElement,
            pose = JsonDocument.Parse(ProjectJson.SerializeEntity(Pose.LookAt(new Vec3d(0, 0, 6), Vec3d.Zero, Vec3d.UnitY))).RootElement,
        });
        var rig = HostRig.Start(p);
        var node = rig.Engine.Services.Settings.NodeId;
        var stopped = false;
        try
        {
            await rig.Ok("BLIND ON");
            await rig.Ok("SET cv_main background \"#FF0000\"");
            await rig.Ok("VIEW draft CAMERA " + eye);
            await rig.Ok("VIEW draft SHOW editor DRAFT");
            var path = rig.Engine.Previews.RingPath("draft");
            var waited = Stopwatch.StartNew();
            var drawn = false;
            while (!drawn && waited.Elapsed < HostRig.Patience)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        using var reader = new PreviewRingReader(path);
                        drawn = reader.TryRead(new byte[reader.MaxWidth * reader.MaxHeight * 4], -1, out _);
                    }
                    catch (InvalidDataException)
                    {
                        // Being made: the next look finds it.
                    }
                }
                if (!drawn)
                    await rig.Host.FramesAsync(1).WaitAsync(HostRig.Patience);
            }
            Assert.True(drawn, "the draft view never drew");
            Assert.Contains(path, FilesOf(node));

            await rig.DisposeAsync();
            stopped = true;

            Assert.Empty(FilesOf(node));
        }
        finally
        {
            if (!stopped)
                await rig.DisposeAsync();
            foreach (var left in FilesOf(node))
                File.Delete(left);
        }
    }

    [Fact]
    public async Task ANodeMakingItsViewsTakesAwayItsOwnEarlierRingsThatNothingHolds()
    {
        var node = Guid.NewGuid().ToString("N")[..8];
        var other = Guid.NewGuid().ToString("N")[..8];
        var gone = PreviewRing.PathFor(node, "cam1", 17);
        var draft = PreviewRing.PathFor(node, "cam1~draft", 18);
        var held = PreviewRing.PathFor(node, "cam2", 19);
        var plain = PreviewRing.PathFor(node, "cam3");
        var others = PreviewRing.PathFor(other, "cam1", 20);
        string[] all = [gone, draft, held, plain, others];
        try
        {
            foreach (var f in all)
                await File.WriteAllBytesAsync(f, new byte[PreviewRing.HeaderBytes]);
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                var services = EngineServices.ForTests(new ManualClock(5_000_000_000));
                await using var engine = new EngineCore(services with { Settings = services.Settings with { NodeId = node } }, Project.CreateDefault("Show"));

                Assert.NotNull(engine.Previews);

                Assert.Equal([held, plain, others], all.Where(File.Exists));
            }
        }
        finally
        {
            foreach (var f in all.Where(File.Exists))
                File.Delete(f);
        }
    }
}
