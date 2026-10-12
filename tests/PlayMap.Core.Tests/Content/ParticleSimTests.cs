using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Content;

/// <summary>The particle field: deterministic for a seed and a step sequence, joinable late, bounded in the
/// catch-up a frame may do, and every one of Patterns' scenes by name.</summary>
public class ParticleSimTests
{
    private static ParticleSettings Snow(int seed = 20260829) =>
        ParticleSettings.FromPreset(ParticlePresets.Find("Snow")!) with { Seed = seed };

    [Fact]
    public void TheSameSeedDrawsTheSameParticlesAtTheSameTime()
    {
        var a = new ParticleSim();
        var b = new ParticleSim();
        a.Configure(Snow(), 640, 360);
        b.Configure(Snow(), 640, 360);
        for (var f = 0; f < 30; f++)
        {
            a.Advance(10 + f / 60.0, EffectImpulse.None);
            b.Advance(10 + f / 60.0, EffectImpulse.None);
        }
        Assert.Equal(a.StepsDone, b.StepsDone);
        Assert.True(a.Particles.SequenceEqual(b.Particles));
    }

    [Fact]
    public void ADifferentSeedDrawsADifferentField()
    {
        var a = new ParticleSim();
        var b = new ParticleSim();
        a.Configure(Snow(1), 640, 360);
        b.Configure(Snow(2), 640, 360);
        Assert.False(a.Particles.SequenceEqual(b.Particles));
    }

    [Fact]
    public void ASimThatJoinsLateShowsTheLeadersFieldAndStaysInStep()
    {
        var leader = new ParticleSim();
        leader.Configure(Snow(), 800, 450);
        for (var f = 0; f < 120; f++)
            leader.Advance(30 + f / 60.0, EffectImpulse.None);

        var late = new ParticleSim();
        late.Configure(Snow(), 800, 450);
        Assert.True(late.JoinTimeline(leader));
        Assert.True(late.Particles.SequenceEqual(leader.Particles));

        for (var f = 120; f < 180; f++)
        {
            leader.Advance(30 + f / 60.0, EffectImpulse.None);
            late.Advance(30 + f / 60.0, EffectImpulse.None);
        }
        Assert.Equal(leader.StepsDone, late.StepsDone);
        Assert.True(late.Particles.SequenceEqual(leader.Particles));
    }

    [Fact]
    public void AJoinAcrossDifferentSettingsIsRefused()
    {
        var leader = new ParticleSim();
        leader.Configure(Snow(), 800, 450);
        leader.Advance(5, EffectImpulse.None);
        var other = new ParticleSim();
        other.Configure(Snow(7), 800, 450);
        Assert.False(other.JoinTimeline(leader));
    }

    [Fact]
    public void ASimBehindTheClockCatchesUpNoFasterThanItsBudget()
    {
        var dense = new ParticleSim();
        dense.Configure(ParticleSettings.FromPreset(ParticlePresets.Find("Starcloth dense")!), 640, 360);
        dense.Advance(100, EffectImpulse.None);
        var anchored = dense.StepsDone;
        // Ten seconds later in one frame: 1200 steps due, the budget allows fewer.
        dense.Advance(110, EffectImpulse.None);
        var stepped = dense.StepsDone - anchored;
        Assert.Equal(dense.StepsPerFrame, stepped);
        Assert.True(stepped * dense.Count <= ParticleSim.CatchUpBudget + ParticleSim.MinStepsPerFrame * dense.Count);
        Assert.True(stepped < 1200);
    }

    [Fact]
    public void AClockThatJumpsFarAheadReanchorsOnTheQuantisedGrid()
    {
        var sim = new ParticleSim();
        sim.Configure(Snow(), 320, 180);
        sim.Advance(1, EffectImpulse.None);
        sim.Advance(1000, EffectImpulse.None);
        var target = (long)(1000 / ParticleSim.StepSeconds);
        var anchor = target / ParticleSim.BaselineQuantum * ParticleSim.BaselineQuantum;
        Assert.InRange(sim.StepsDone, anchor, target);
    }

    [Fact]
    public void AllThirtyFourScenesExistByNameAndRun()
    {
        Assert.Equal(34, ParticlePresets.All.Count);
        Assert.Equal(34, ParticlePresets.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(8, ParticlePresets.Categories.Count);
        var buffer = new float[ParticleSettings.MaxCount * ParticleSim.InstanceFloats];
        foreach (var name in ParticlePresets.Names)
        {
            var settings = ParticleSettings.Resolve(ParamSet.Empty.With("preset", name));
            var sim = new ParticleSim();
            sim.Configure(settings, 320, 180);
            sim.Advance(12.5, EffectImpulse.None);
            var n = sim.WriteInstances(buffer);
            Assert.True(n > 0, name);
            for (var i = 0; i < n * ParticleSim.InstanceFloats; i++)
                Assert.True(float.IsFinite(buffer[i]), name);
        }
    }

    [Fact]
    public void APresetFillsWhatTheOperatorDidNotSet()
    {
        var rain = ParticleSettings.Resolve(ParamSet.Empty.With("preset", "Rain"));
        Assert.Equal(1400, rain.Count);
        Assert.Equal(ParticleShape.Streak, rain.Shape);
        var mine = ParticleSettings.Resolve(ParamSet.Empty.With("preset", "Rain").With("count", 50).With("shape", "star"));
        Assert.Equal(50, mine.Count);
        Assert.Equal(ParticleShape.Star, mine.Shape);
        Assert.Equal(rain.SpeedMax, mine.SpeedMax);
        var none = ParticleSettings.Resolve(ParamSet.Empty.With("preset", "no such scene"));
        Assert.Equal(800, none.Count);
    }

    [Fact]
    public void AStingBurstRebirthsPartOfTheFieldTheSameWayOnEveryNode()
    {
        var a = new ParticleSim();
        var b = new ParticleSim();
        var calm = new ParticleSim();
        a.Configure(Snow(), 640, 360);
        b.Configure(Snow(), 640, 360);
        calm.Configure(Snow(), 640, 360);
        var sting = new EffectImpulse(PulsePreset.Explosion, 20.1, 1.0);
        for (var f = 0; f < 60; f++)
        {
            a.Advance(20 + f / 60.0, sting);
            b.Advance(20 + f / 60.0, sting);
            calm.Advance(20 + f / 60.0, EffectImpulse.None);
        }
        Assert.True(a.Particles.SequenceEqual(b.Particles));
        Assert.False(a.Particles.SequenceEqual(calm.Particles));
    }
}
