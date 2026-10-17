using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Core.Storage;
using PlayMap.Engine.Align;
using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The files the node renames into place reach the disk before the rename: the node's settings as the host persists them
/// (a backup's takeover epoch rides there), the recovery record, the optimise queue and the saved project are each reported flushed
/// under their final path, and hold what was written; an align run's folder is flushed whole beside its place before it moves in.</summary>
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

    [Fact]
    public void AnAlignRunsFolderIsFlushedWholeBesideItsPlaceBeforeItMovesIn()
    {
        var project = Directory.CreateTempSubdirectory("playmap-align-durable-").FullName;
        try
        {
            var record = new AlignRunRecord("ar0a1b2c3d", "ag1", "staged", 1, 2, "cam1", "planar", "staged", [], QualityScore.None, 0, 1, 0.5, 0);
            var final = Path.Combine(project, AlignArchive.Folder, record.Id);
            var temp = Path.Combine(project, AlignArchive.Folder, "." + record.Id + ".partial");
            var seen = new List<(string Path, bool InPlace, string Beside)>();
            void Flushed(string path)
            {
                if (path.StartsWith(project, StringComparison.Ordinal))
                    seen.Add((path, Directory.Exists(final), string.Join(' ', Directory.GetFiles(temp).Select(Path.GetFileName).Order(StringComparer.Ordinal))));
            }
            DurableFile.Flushed += Flushed;
            try
            {
                AlignArchive.Save(project, record, [("warp-op1.pfm", [1, 2, 3]), ("blend-op1.pfm", [4, 5])]);
            }
            finally
            {
                DurableFile.Flushed -= Flushed;
            }
            Assert.Equal([(final, false, "blend-op1.pfm run.json warp-op1.pfm")], seen);
            Assert.False(Directory.Exists(temp));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(final, "warp-op1.pfm")));
            Assert.Equal("staged", AlignArchive.Load(project, record.Id)!.State);
            Assert.Throws<IOException>(() => AlignArchive.Save(project, record, []));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}
