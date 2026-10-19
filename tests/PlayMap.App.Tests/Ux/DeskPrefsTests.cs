using PlayMap.App.Infrastructure;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests.Ux;

/// <summary>desk.json as a person may leave it: a key of the wrong shape (a hand edit) reads as its default and every other key is kept,
/// so the desk starts; a write goes beside the file and is renamed over it, so a crash mid-write leaves the last whole file.</summary>
public class DeskPrefsTests
{
    private static DeskPrefs LoadText(DirectoryInfo dir, string json)
    {
        var path = Path.Combine(dir.FullName, "desk.json");
        File.WriteAllText(path, json);
        return DeskPrefs.Load(path);
    }

    [Fact]
    public void AKeyOfTheWrongShapeReadsAsItsDefaultAndTheRestAreKept()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-prefs-");
        try
        {
            // A brightness with a fraction is rounded and clamped, never a failed start.
            var prefs = LoadText(dir, """{"brightness": 55.5, "theme": "dim", "pro": true}""");
            Assert.Equal(56, prefs.Brightness);
            Assert.Equal(DeskTheme.Dim, prefs.Theme);
            Assert.True(prefs.IsPro);
            Assert.Equal(20, LoadText(dir, """{"brightness": 3.2}""").Brightness);
            Assert.Equal(100, LoadText(dir, """{"brightness": 1e300}""").Brightness);
            Assert.Equal(100, LoadText(dir, """{"brightness": "bright"}""").Brightness);

            // quadDrafts that is no object, or a node that is no object: no drafts, and the other keys kept.
            prefs = LoadText(dir, """{"quadDrafts": [], "theme": "daylight"}""");
            Assert.Empty(prefs.QuadDraftProjectors("demo0001"));
            Assert.Null(prefs.QuadDraft("demo0001", "pj_left"));
            Assert.Equal(DeskTheme.Daylight, prefs.Theme);
            prefs = LoadText(dir, """{"quadDrafts": {"n": 5, "m": {"pj": 7, "pk": [[0.1, "x"], [0.2, 0.3]]}}}""");
            Assert.Empty(prefs.QuadDraftProjectors("n"));
            Assert.Null(prefs.QuadDraft("n", "pj"));
            Assert.Null(prefs.QuadDraft("m", "pj"));
            Assert.Equal([new Vec2d(0.2, 0.3)], prefs.QuadDraft("m", "pk")!);

            // A draft kept over the bad shape writes a good one, and the file reads back whole.
            prefs.SetQuadDraft("n", "pj_left", [new Vec2d(0.25, 0.5)]);
            var again = DeskPrefs.Load(Path.Combine(dir.FullName, "desk.json"));
            Assert.Equal([new Vec2d(0.25, 0.5)], again.QuadDraft("n", "pj_left")!);
            Assert.Empty(Directory.EnumerateFiles(dir.FullName, "*.saving"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
