namespace PlayMap.Engine.Tests.Facade;

/// <summary>A survey import and the life of the node and the project around it: a node disposed while an import runs has ended the
/// import by the time its disposal returns, with nothing the import wrote left beside the project; an import that finishes after
/// another project was opened, or after the project was saved into another folder, is not applied to it — its mesh and cloud were
/// written beside the project it was asked for — and says so, its files removed.</summary>
[Collection(SurveyPerfCollection.Name)]
public sealed class SurveyLifecycleTests
{
    private static void AssertNothingWritten(string folder)
    {
        foreach (var sub in new[] { "meshes", "clouds" })
        {
            var dir = Path.Combine(folder, sub);
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(), $"{sub}: {(Directory.Exists(dir) ? string.Join(", ", Directory.EnumerateFiles(dir)) : "")}");
        }
    }

    [Fact]
    public async Task ADisposedNodeHasEndedItsImportAndLeftNothingItWrote()
    {
        var rig = new SiteRig();
        try
        {
            var file = SurveyFacade.WritePly(rig.File("front.ply"), 601, 601);
            await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL DECIMATE 20000");
            await rig.Engine.DisposeAsync();
            Assert.True(rig.Engine.WhenSurveyed.IsCompleted, "the import still runs after the node was disposed");
            Assert.Equal("failed", rig.Engine.SurveyJob!.State);
            Assert.Contains("stopped", rig.Engine.SurveyJob.Words, StringComparison.Ordinal);
            AssertNothingWritten(rig.Folder);
        }
        finally
        {
            Directory.Delete(rig.Folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnImportIsNotAppliedToAnotherProjectOpenedWhileItRan()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("front.ply"), 401, 401);
        await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL DECIMATE 20000");
        await rig.Ok("PROJECT NEW Another show");
        var revision = rig.Engine.Store.Current.Revision;
        await rig.Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal("failed", rig.Engine.SurveyJob!.State);
        Assert.Contains("another project", rig.Engine.SurveyJob.Words, StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Empty(rig.Engine.Store.Project.Stage.Surveys);
        Assert.Empty(rig.Engine.Store.Project.Stage.Meshes);
        AssertNothingWritten(rig.Folder);
    }

    [Fact]
    public async Task AnImportIsNotAppliedToAProjectSavedIntoAnotherFolderWhileItRan()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("front.ply"), 401, 401);
        await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL DECIMATE 20000");
        var elsewhere = Path.Combine(rig.Folder, "elsewhere", "show.playmap");
        await rig.Ok($"PROJECT SAVE {elsewhere}");
        var revision = rig.Engine.Store.Current.Revision;
        await rig.Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal("failed", rig.Engine.SurveyJob!.State);
        Assert.Contains("another folder", rig.Engine.SurveyJob.Words, StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Empty(rig.Engine.Store.Project.Stage.Surveys);
        AssertNothingWritten(rig.Folder);
    }
}
