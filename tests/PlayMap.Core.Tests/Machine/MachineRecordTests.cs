using System.Collections.Immutable;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Machine;

/// <summary>The machine's shared records: every edition family, thread role, vendor tier and support level has its STATE word and
/// reads back from it; an unread identity is unknown, never guessed; a capability snapshot finds a probe by feature and adapter; the
/// null platform claims nothing and says so; its timer never answers before its instant and answers false on cancel.</summary>
public sealed class MachineRecordTests
{
    [Fact]
    public void EveryEditionFamilyHasItsWordAndReadsBackFromIt()
    {
        Assert.Equal(["unknown", "home", "pro", "workstation", "enterprise", "education", "iot", "server"],
            Enum.GetValues<EditionFamily>().Select(OsIdentity.Word));
        foreach (var family in Enum.GetValues<EditionFamily>())
        {
            Assert.True(OsIdentity.TryParseFamily(OsIdentity.Word(family).ToUpperInvariant(), out var read));
            Assert.Equal(family, read);
        }
        foreach (var word in new[] { null, "", "core", "professional", "ltsc", "iot enterprise" })
        {
            Assert.False(OsIdentity.TryParseFamily(word, out var read), word);
            Assert.Equal(EditionFamily.Unknown, read);
        }
        // IoT Enterprise LTSC is the IoT family with both flags; Enterprise LTSC is Enterprise with the LTSC flag alone.
        var iotLtsc = new OsIdentity("IoTEnterpriseS", EditionFamily.Iot, "24H2", 26100, 4061, Ltsc: true, Iot: true);
        var enterpriseLtsc = new OsIdentity("EnterpriseS", EditionFamily.Enterprise, "24H2", 26100, 4061, Ltsc: true, Iot: false);
        Assert.Equal("iot", iotLtsc.FamilyWord);
        Assert.True(iotLtsc.Ltsc && iotLtsc.Iot);
        Assert.Equal("enterprise", enterpriseLtsc.FamilyWord);
        Assert.True(enterpriseLtsc.Ltsc);
        Assert.False(enterpriseLtsc.Iot);
    }

    [Fact]
    public void EveryRoleTierAndSupportLevelHasItsWord()
    {
        Assert.Equal(["render", "present", "decode", "audio", "timecode", "control", "led", "network", "background", "capture"],
            Enum.GetValues<ThreadRole>().Select(ThreadClaims.Word));
        Assert.Equal(["other", "nvidiaRtxPro", "nvidiaRtx", "radeon", "intelArc", "software"], Enum.GetValues<VendorTier>().Select(AdapterIdentity.Word));
        Assert.Equal(["unsupported", "tier1", "validatedPerDriver", "bestEffort"], Enum.GetValues<SupportLevel>().Select(AdapterIdentity.Word));
        var adapter = new AdapterIdentity(0x123a4, 4318, 0x2bb1, "NVIDIA RTX PRO 4000 Blackwell", "582.16", "r580", VendorTier.NvidiaRtxPro,
            SupportLevel.Tier1, DrivesOutputs: true, DedicatedMb: 24_576);
        Assert.Equal("0x00000000000123a4", adapter.LuidText);
        Assert.Equal("nvidiaRtxPro", adapter.TierWord);
        Assert.Equal("tier1", adapter.SupportWord);
    }

    [Fact]
    public void AnUnreadIdentityIsUnknownAndASnapshotFindsAProbeByFeatureAndAdapter()
    {
        var unread = NodeIdentity.Unread;
        Assert.Equal(BuildInfo.Current, unread.Build);
        Assert.Equal(EditionFamily.Unknown, unread.Os.Family);
        Assert.Equal("", unread.Os.Edition);
        Assert.Equal(0, unread.Os.Build);
        Assert.Equal("", unread.Cpu.Model);
        Assert.Empty(unread.Adapters);
        Assert.Equal(RuntimeIdentity.Empty, unread.Runtimes);

        var barrier = new CapabilityProbe("presentBarrier", 0x123a4, true, "", "582.16");
        var hags = new CapabilityProbe("hags", 0, false, "hardware scheduling is off", "");
        var snapshot = new CapabilitySnapshot(1_760_600_000_000, [barrier, hags]);
        Assert.Equal(barrier, snapshot.Find("presentBarrier", 0x123a4));
        Assert.Equal(hags, snapshot.Find("hags"));
        Assert.Null(snapshot.Find("presentBarrier"));
        Assert.Null(snapshot.Find("hdr10", 0x123a4));
        Assert.Empty(CapabilitySnapshot.Empty.Probes);
        Assert.Null(CapabilitySnapshot.Empty.Find("hags"));
    }

    [Fact]
    public void TheNullPlatformClaimsNothingAndSaysSo()
    {
        using var platform = new NullMachinePlatform();
        Assert.Equal("none", platform.Name);
        Assert.Equal(NullMachinePlatform.NotOnThisNodeWords, platform.Words);

        var process = Assert.Single(platform.ClaimProcess(MachineSettings.Default));
        Assert.False(process.Applied);
        Assert.Equal(platform.Words, process.Words);
        Assert.Equal("", process.ReadBack);

        foreach (var role in Enum.GetValues<ThreadRole>())
        {
            using var claim = platform.ClaimThread(role);
            var c = Assert.Single(claim.Claims);
            Assert.Equal("thread." + ThreadClaims.Word(role), c.What);
            Assert.False(c.Applied);
            Assert.Equal(platform.Words, c.Words);
        }

        using (platform.KeepAwake())
            Assert.Equal(CapabilitySnapshot.Empty, platform.Capabilities());
        var fact = Assert.Single(platform.Facts());
        Assert.Equal(new MachineFact("platform", Light.Grey, "none", platform.Words), fact);

        // The identity carries this build and what the runtime itself reports; the operating system's facts stay unread.
        var identity = platform.Identity();
        Assert.Equal(BuildInfo.Current, identity.Build);
        Assert.Equal(OsIdentity.Unknown, identity.Os);
        Assert.Equal(Environment.ProcessorCount, identity.Cpu.Logical);
        Assert.Contains(identity.Cpu.VectorBits, new[] { 0, 128, 256, 512 });
        Assert.Equal("", identity.Cpu.Model);
        Assert.Empty(identity.Cpu.ByEfficiencyClass);
        Assert.Empty(identity.Adapters);

        using var words = new NullMachinePlatform("the platform seam is Windows only");
        Assert.Equal("the platform seam is Windows only", Assert.Single(words.ClaimThread(ThreadRole.Render).Claims).Words);
        Assert.Throws<ArgumentException>(() => new NullMachinePlatform(" "));
    }

    [Fact]
    public void TheRuntimeTimerNeverAnswersBeforeItsInstantAndAnswersFalseOnCancel()
    {
        var clock = MonoClock.Instance;
        using var timer = new RuntimeDeadlineTimer(clock);
        Assert.Equal(0, timer.LateP99Us);

        // An instant already past answers at once; one a few milliseconds on answers at or after it (never before). How late it woke
        // is recorded, with no ceiling: the machine's scheduler decides that.
        Assert.True(timer.WaitUntil(clock.NowNs - 1_000_000));
        for (var i = 0; i < 5; i++)
        {
            var at = clock.NowNs + 2_000_000;
            Assert.True(timer.WaitUntil(at));
            Assert.True(clock.NowNs >= at);
        }
        Assert.True(timer.LateP99Us >= 0);

        using var cancel = new ManualResetEvent(true);
        Assert.False(timer.WaitUntil(clock.NowNs + 60_000_000_000, cancel));
        Assert.False(timer.WaitUntil(clock.NowNs - 1, cancel));
    }

    [Fact]
    public void ADeadlineWeeksAwayIsWaitedForInStepsTheRuntimeTakesAndACancelStillEndsIt()
    {
        // A wait handle takes at most int.MaxValue ms (about 24.8 days) at once: a deadline 30 days out waits in such steps.
        var clock = new ManualClock(1_000_000_000);
        using var timer = new RuntimeDeadlineTimer(clock);
        using var cancel = new ManualResetEvent(true);

        Assert.False(timer.WaitUntil(clock.NowNs + 30L * 24 * 3600 * 1_000_000_000, cancel));
        Assert.False(timer.WaitUntil(long.MaxValue, cancel));
        Assert.Equal(0, timer.LateP99Us);
    }
}

/// <summary>ThreadClaims is one static seam for the process: a claim goes to the platform in use, and before any, changes nothing and
/// says so. Every test that points it at a platform joins this collection, which runs alone.</summary>
[Collection(ThreadClaimsCollection.Name)]
public sealed class ThreadClaimsTests
{
    private sealed class Recording : IMachinePlatform
    {
        public List<ThreadRole> Roles { get; } = [];

        public string Name => "recording";

        public string Words => "";

        public ImmutableArray<Claim> ClaimProcess(MachineSettings settings) => [];

        public IThreadClaim ClaimThread(ThreadRole role)
        {
            Roles.Add(role);
            return new UnclaimedThread(role, "recorded");
        }

        public IDeadlineTimer CreateTimer() => new RuntimeDeadlineTimer();

        public IDisposable KeepAwake() => new Held();

        public NodeIdentity Identity() => NodeIdentity.Unread;

        public CapabilitySnapshot Capabilities() => CapabilitySnapshot.Empty;

        public ImmutableArray<MachineFact> Facts() => [];

        public void Dispose()
        {
            // Nothing is held.
        }

        private sealed class Held : IDisposable
        {
            public void Dispose()
            {
                // Nothing is held.
            }
        }
    }

    [Fact]
    public void AClaimGoesToThePlatformInUseAndBeforeAnyChangesNothingAndSaysSo()
    {
        if (ThreadClaims.Platform is null)
        {
            using var before = ThreadClaims.Claim(ThreadRole.Decode);
            var c = Assert.Single(before.Claims);
            Assert.Equal(new Claim("thread.decode", "decode", false, "", ThreadClaims.NoPlatformWords), c);
        }

        using var platform = new Recording();
        ThreadClaims.Use(platform);
        Assert.Same(platform, ThreadClaims.Platform);
        using (var claim = ThreadClaims.Claim(ThreadRole.Capture))
            Assert.Equal("recorded", Assert.Single(claim.Claims).Words);
        Assert.Equal([ThreadRole.Capture], platform.Roles);
        Assert.Throws<ArgumentNullException>(() => ThreadClaims.Use(null!));

        // Pointed at a platform that claims nothing, a claim says that platform's words.
        using var none = new NullMachinePlatform("the platform seam is Windows only");
        ThreadClaims.Use(none);
        using var after = ThreadClaims.Claim(ThreadRole.Render);
        Assert.Equal("the platform seam is Windows only", Assert.Single(after.Claims).Words);
        Assert.Equal([ThreadRole.Capture], platform.Roles);
    }

    [Fact]
    public void AThreadsDeadlineTimerOnAClockWaitsOnThatClock()
    {
        var clock = new ManualClock(1_000_000_000);
        using var timer = ThreadClaims.CreateTimer(clock);
        Assert.IsType<RuntimeDeadlineTimer>(timer);
        // An instant the clock has reached answers at once; one it has not waits until the cancel, however long the machine runs.
        Assert.True(timer.WaitUntil(1_000_000_000));
        using var cancel = new ManualResetEvent(true);
        Assert.False(timer.WaitUntil(2_000_000_000, cancel));
        Assert.Throws<ArgumentNullException>(() => ThreadClaims.CreateTimer(null!));
    }
}

/// <summary>The tests that point the process's thread claims at a platform: they run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadClaimsCollection
{
    public const string Name = "Thread claims";
}
