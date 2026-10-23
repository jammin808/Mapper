using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Engine.Host;
using PlayMap.Net.Bus;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The engine's pid record as a node comes up: written into the engine's folder where it exists, with this process, the line
/// wire's port as it listens and the start, and taken back on a clean stop; nothing written, and no folder made, where it does not
/// exist or the host names none; only an engine the supervisor started (--supervised) names the machine's folders — the engine's for
/// its record, the supervisor's above it for the status it reads — so no other run takes the supervised engine's record.</summary>
public sealed class PidRecordHostTests
{
    private static async Task<EngineHost> StartAsync(DirectoryInfo dir, string? supervisor)
    {
        var home = new NodeHome(Path.Combine(dir.FullName, "node"));
        home.SaveSettings(NodeSettings.CreateDefault("Pid") with
        {
            Bind = "127.0.0.1", HttpPort = 0, WirePort = 0, SyncPort = 0, Token = "K7QM-3XWD-P9RA", TrustLoopback = true, Announce = false,
        });
        var options = EngineOptions.Parse(["engine", "headless", "--home", home.Folder, "--no-recover"], out var error);
        Assert.Null(error);
        var host = new EngineHost(options, home, new EngineHostOptions
        {
            Hello = new HelloOptions { ListenPort = 0, Targets = [], Every = TimeSpan.FromMilliseconds(200), SweepEvery = TimeSpan.FromMilliseconds(200) },
            SupervisorFolder = supervisor,
        });
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task TheRecordNamesThisProcessTheWiresPortAndTheStartAndGoesOnACleanStop()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-pid-host-");
        try
        {
            var supervisor = Directory.CreateDirectory(Path.Combine(dir.FullName, "supervisor")).FullName;
            var path = Path.Combine(supervisor, EnginePidRecord.FileName);
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var host = await StartAsync(dir, supervisor);
            try
            {
                var record = EnginePidRecord.Parse(File.ReadAllText(path));
                Assert.NotNull(record);
                Assert.Equal(Environment.ProcessId, record.Pid);
                Assert.True(host.Network.WirePort > 0);
                Assert.Equal(host.Network.WirePort, record.WirePort);
                Assert.InRange(record.StartedUnixMs, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Assert.DoesNotContain(host.Faults, f => f.Part == "supervisor");
            }
            finally
            {
                await host.DisposeAsync();
            }
            Assert.False(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NoFolderNoRecordAndNoFolderMade()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-pid-host-");
        try
        {
            var missing = Path.Combine(dir.FullName, "supervisor");
            await using (var host = await StartAsync(dir, missing))
            {
                Assert.False(Directory.Exists(missing));
                Assert.DoesNotContain(host.Faults, f => f.Part == "supervisor");
            }
            var none = await StartAsync(dir, null);
            await none.DisposeAsync();
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void OnlyAnEngineTheSupervisorStartedNamesTheMachinesFolder()
    {
        var plain = EngineOptions.Parse(["engine", "headless"], out var error);
        Assert.Null(error);
        Assert.False(plain.Supervised);
        Assert.Null(EngineEntry.SupervisorFolderFor(plain));
        Assert.Null(EngineEntry.StatusFolderFor(plain));

        var supervised = EngineOptions.Parse(["engine", "headless", "--supervised"], out error);
        Assert.Null(error);
        Assert.True(supervised.Supervised);
        Assert.Equal(EnginePidRecord.MachineFolder(), EngineEntry.SupervisorFolderFor(supervised));
        // The status is read from the supervisor's folder, the record written into the engine's folder inside it.
        Assert.Equal(SupervisorStatus.MachineFolder(), EngineEntry.StatusFolderFor(supervised));
        Assert.Contains("[--supervised]", EngineEntry.Usage, StringComparison.Ordinal);
    }
}
