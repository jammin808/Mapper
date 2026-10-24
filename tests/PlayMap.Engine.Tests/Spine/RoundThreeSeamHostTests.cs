using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Engine.Host;
using PlayMap.Net.Bus;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>A node composes round 3's seams from the start: a host's services carry each at its null implementation (no observer, no
/// preview share, no GPU lifecycle listener, Core's null machine platform), and the host points the process's thread claims at the
/// platform its services carry once it starts — the null one, or the one an extension's Configure set. ThreadClaims is one static
/// seam for the process, so these run alone.</summary>
[Collection(SeamHostCollection.Name)]
public sealed class RoundThreeSeamHostTests
{
    /// <summary>A platform a test can tell apart: it claims nothing, under its own words.</summary>
    private sealed class Extension(IMachinePlatform platform) : IEngineExtension
    {
        public string Name => "platform";

        public EngineServices Configure(EngineServices services) => services with { MachinePlatform = platform };

        public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<EngineHost> StartAsync(DirectoryInfo dir, EngineParts parts)
    {
        var home = new NodeHome(Path.Combine(dir.FullName, "node"));
        home.SaveSettings(NodeSettings.CreateDefault("Seams") with
        {
            Bind = "127.0.0.1", HttpPort = 0, WirePort = 0, SyncPort = 0, Token = "K7QM-3XWD-P9RA", TrustLoopback = true, Announce = false,
        });
        var options = EngineOptions.Parse(["engine", "headless", "--home", home.Folder, "--no-recover"], out var error);
        Assert.Null(error);
        var host = new EngineHost(options, home, new EngineHostOptions
        {
            Parts = parts,
            Hello = new HelloOptions { ListenPort = 0, Targets = [], Every = TimeSpan.FromMilliseconds(200), SweepEvery = TimeSpan.FromMilliseconds(200) },
        });
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task AHostsServicesCarryTheNullSeamsAndItsThreadClaimsGoToItsPlatform()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-seams-");
        try
        {
            await using (var host = await StartAsync(dir, EngineParts.Bare))
            {
                var services = host.Engine.Services;
                Assert.Same(NoAirObserver.Instance, services.Observer);
                Assert.Same(NoPreviewShare.Instance, services.PreviewShare);
                Assert.Empty(services.GpuLifecycles);
                Assert.IsType<NullMachinePlatform>(services.MachinePlatform);
                Assert.Same(services.MachinePlatform, ThreadClaims.Platform);
                using var claimed = ThreadClaims.Claim(ThreadRole.Control);
                var claim = Assert.Single(claimed.Claims);
                Assert.False(claim.Applied);
                Assert.Equal(NullMachinePlatform.NotOnThisNodeWords, claim.Words);
            }

            using var own = new NullMachinePlatform("a test's platform");
            var parts = EngineParts.Bare with { Extensions = [_ => new Extension(own)] };
            await using (var host = await StartAsync(dir, parts))
            {
                Assert.Same(own, host.Engine.Services.MachinePlatform);
                Assert.Same(own, ThreadClaims.Platform);
                using var claimed = ThreadClaims.Claim(ThreadRole.Render);
                Assert.Equal("a test's platform", Assert.Single(claimed.Claims).Words);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AnEngineMadeWithoutAHostCarriesTheNullSeamsToo()
    {
        var services = EngineServices.ForTests();
        Assert.Same(NoAirObserver.Instance, services.Observer);
        Assert.Same(NoPreviewShare.Instance, services.PreviewShare);
        Assert.Empty(services.GpuLifecycles);
        Assert.Equal("none", services.MachinePlatform.Name);
    }
}

/// <summary>The tests that point the process's thread claims at a platform run one at a time, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SeamHostCollection
{
    public const string Name = "seam hosts";
}
