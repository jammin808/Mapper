using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Storage;
using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The files the node renames into place reach the disk before the rename: the node's settings as the host persists them
/// (a backup's takeover epoch rides there), the recovery record, the optimise queue and the saved project are each reported flushed
/// under their final path, and hold what was written.</summary>
public sealed class DurableWriteTests
{
    [Fact]
    public async Task TheSettingsTheRecoveryRecordTheQueueAndTheProjectAreFlushedBeforeTheyAreRenamed()
    {
        var flushed = new ConcurrentQueue<string>();
        await using var node = await HostRig.StartAsync("Durable node");
        var home = node.Home.Folder;
        void Flushed(string path)
        {
            if (path.StartsWith(home, StringComparison.Ordinal) || path.StartsWith(node.Dir.FullName, StringComparison.Ordinal))
                flushed.Enqueue(path);
        }
        DurableFile.Flushed += Flushed;
        try
        {
            Assert.Equal("", node.Network.PersistSettings(s => s with { Name = "Durable node, renamed" }));
            Assert.Contains(node.Home.SettingsPath, flushed);
            Assert.Equal("Durable node, renamed", node.Home.LoadSettings("x").Name);

            RecoveryRecord.Capture(node.Engine).Save(node.Home.RecoveryPath);
            Assert.Contains(node.Home.RecoveryPath, flushed);

            var queue = new JobStore(Path.Combine(home, "optimise"), NullLogger.Instance);
            queue.Save([]);
            Assert.Contains(queue.Path, flushed);

            var project = Path.Combine(node.Dir.FullName, "show.playmap");
            await node.OkAsync("PROJECT SAVE " + project);
            Assert.Contains(project, flushed);
            Assert.True(File.Exists(project));
        }
        finally
        {
            DurableFile.Flushed -= Flushed;
        }
    }
}
