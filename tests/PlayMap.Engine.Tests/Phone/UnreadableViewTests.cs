using System.Text.Json;
using PlayMap.Core.Model;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A scan read back from its folder says what did not read: a view whose decodes are gone or damaged is listed "unreadable" with the
/// words why (never left out of the list unsaid), takes no part in a solve — which says so — and its folder is never written over; a
/// view missing one projector's decode names that decode.
/// </summary>
public sealed class UnreadableViewTests(ITestOutputHelper output)
{
    private const string Pj1 = MultiViewScanKit.Pj1;
    private const string Pj2 = MultiViewScanKit.Pj2;

    private static string Line(string? into = null) =>
        $"SCAN START {MultiViewScanKit.Camera} {Pj1} {Pj2}" + (into is null ? "" : " INTO " + into);

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
    }

    private static Dictionary<string, JsonElement> Views(JsonElement reply) =>
        reply.GetProperty("views").EnumerateArray().ToDictionary(v => v.GetProperty("id").GetString()!, v => v.Clone(), StringComparer.Ordinal);

    private static Dictionary<string, byte[]> Files(string folder) =>
        Directory.EnumerateFiles(folder).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);

    [Fact]
    public async Task AViewWhoseDecodesDoNotReadIsListedUnreadableTakesNoPartAndIsNeverWrittenOver()
    {
        // The first node: two views of the scan, kept in its folder.
        var first = new OpsRig(project: MultiViewScanKit.Project());
        string id;
        Project project;
        try
        {
            id = await MultiViewScanKit.ScanAsync(first, Line());
            Assert.Equal(id, await MultiViewScanKit.ScanAsync(first, Line(id)));
            Assert.Equal(["v1", "v2"], first.Scanner.Find(id)!.Multi!.ViewIds.ToArray());
            project = first.Engine.Store.Project;
        }
        finally
        {
            await first.DisposeAsync();
        }

        // A new node on the same folder, v2's decodes damaged: one cut short, one gone.
        var second = new OpsRig(project: project);
        try
        {
            CopyFolder(Path.Combine(first.Folder, "scans"), Path.Combine(second.Folder, "scans"));
            var v2Folder = Path.Combine(second.Folder, "scans", id, "views", "v2");
            var pmcm = Path.Combine(v2Folder, Pj1 + ".pmcm");
            File.WriteAllBytes(pmcm, File.ReadAllBytes(pmcm)[..100]);
            File.Delete(Path.Combine(v2Folder, Pj2 + ".pmcm"));
            var damaged = Files(v2Folder);

            var listed = Views(JsonDocument.Parse(await second.Ok("SCAN VIEWS " + id)).RootElement);
            Assert.Equal(["v1", "v2"], listed.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("done", listed["v1"].GetProperty("state").GetString());
            Assert.Equal("unreadable", listed["v2"].GetProperty("state").GetString());
            var words = listed["v2"].GetProperty("words").GetString()!;
            output.WriteLine(words);
            Assert.Contains(Pj1 + ".pmcm", words, StringComparison.Ordinal);
            Assert.Contains(Pj2 + ".pmcm", words, StringComparison.Ordinal);

            // Solved again, v2 takes no part and stays unreadable; the reply says so; its folder is as it was.
            var solved = await second.Run("SCAN SOLVE " + id);
            Assert.True(solved.Ok, solved.Payload);
            output.WriteLine(solved.Effect);
            Assert.Contains("v2 did not read", solved.Effect, StringComparison.Ordinal);
            var after = second.Scanner.Find(id)!;
            Assert.Equal(["v1"], after.Multi!.ViewIds.ToArray());
            Assert.Equal("unreadable", after.Captured.Single(v => v.Id == "v2").State);
            Assert.Equal("unreadable", Views(JsonDocument.Parse(solved.Payload!).RootElement)["v2"].GetProperty("state").GetString());
            Assert.Equal(damaged, Files(v2Folder));
            Assert.Contains("v2 did not read", second.Engine.Store.Project.Stage.Scans.Single().Notes, StringComparison.Ordinal);

            // It cannot be dropped: it takes no part already.
            var drop = await second.Run($"SCAN DROP {id} v2");
            Assert.False(drop.Ok);
            Assert.Contains("did not read", drop.Payload, StringComparison.Ordinal);
            project = second.Engine.Store.Project;
        }
        finally
        {
            await second.DisposeAsync();
        }

        // A third node, v1's decode of Right gone too: v1 still reads (Left's decode) and names the decode it lacks.
        var third = new OpsRig(project: project);
        try
        {
            CopyFolder(Path.Combine(second.Folder, "scans"), Path.Combine(third.Folder, "scans"));
            File.Delete(Path.Combine(third.Folder, "scans", id, "views", "v1", Pj2 + ".pmcm"));
            var listed = Views(JsonDocument.Parse(await third.Ok("SCAN VIEWS " + id)).RootElement);
            Assert.Equal("done", listed["v1"].GetProperty("state").GetString());
            Assert.Contains(Pj2 + ".pmcm", listed["v1"].GetProperty("words").GetString()!, StringComparison.Ordinal);
            Assert.Equal("unreadable", listed["v2"].GetProperty("state").GetString());
            Assert.Equal([Pj1], third.Scanner.Find(id)!.Captured.Single(v => v.Id == "v1").Maps.Keys.ToArray());
        }
        finally
        {
            await third.DisposeAsync();
            first.DeleteFolder();
            second.DeleteFolder();
            third.DeleteFolder();
        }
    }
}
