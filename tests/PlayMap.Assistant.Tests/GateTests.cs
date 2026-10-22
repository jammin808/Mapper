using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>The gate reads risk from the catalogue, refuses what is not a verb, what lacks its required words,
/// what names an id the project does not hold and what would carry a secret; high risk never runs without a
/// person whatever the settings say; secrets are masked; times of day resolve on the node's clock.</summary>
public class GateTests
{
    [Theory]
    [InlineData("VIDEO PLAY", VerbRisk.High)]
    [InlineData("vt play", VerbRisk.High)]
    [InlineData("PLAYLIST NEW Walk-in", VerbRisk.Low)]
    [InlineData("SET sf00000001 opacity 0.5", VerbRisk.Review)]
    [InlineData("BLEND SOLVE ALL", VerbRisk.Review)]
    [InlineData("STATE", VerbRisk.Low)]
    public void TheGateReadsEachVerbsRiskFromTheCatalogue(string line, VerbRisk risk)
    {
        var check = CommandGate.Check(line, SampleShow.Project());

        Assert.True(check.Ok, check.Reason);
        Assert.Equal(risk, check.Risk);
    }

    [Fact]
    public void AVerbTheCatalogueDoesNotKnowIsHighRisk()
    {
        Assert.Equal(VerbRisk.High, CommandGate.RiskOf("TELEPORT"));
        Assert.Equal(VerbRisk.Low, CommandGate.RiskOf("playlist new"));
        var refused = CommandGate.Check("TELEPORT ALL");
        Assert.False(refused.Ok);
        Assert.Equal(VerbRisk.High, refused.Risk);
    }

    [Theory]
    [InlineData("AUTH 0badc0de0badc0de", "the connection's own verb")]
    [InlineData("HELLO me", "the connection's own verb")]
    [InlineData("BLACKOUT MAYBE", "takes ON|OFF|TOGGLE there")]
    [InlineData("OUTPUT ENABLE op00000001", "needs ON|OFF")]
    [InlineData("SURFACE SOURCE sf00000001", "needs <source>")]
    [InlineData("PLAYLIST NEW one\nVIDEO PLAY", "one verb per line")]
    [InlineData("", "an empty line")]
    [InlineData("@bad! VIDEO PLAY", "a request id is @")]
    public void TheGateRefusesWithAReasonTheModelCanActOn(string line, string reason)
    {
        var check = CommandGate.Check(line);

        Assert.False(check.Ok);
        Assert.Contains(reason, check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineWithItsVerbsSecretWordsPassesMarkedAndOneWithoutThemDoesNot()
    {
        var link = CommandGate.Check("LINK PATTERNS 10.0.0.5:9697 0badc0de0badc0de");
        Assert.True(link.Ok, link.Reason);
        Assert.True(link.HoldsSecret);
        Assert.False(CommandGate.Check("LINK PATTERNS 10.0.0.5:9697").HoldsSecret);
        // The same row's follow switch is a keyword and ON|OFF: it holds no secret, so it is decided by its risk like any line.
        Assert.False(CommandGate.Check("LINK PATTERNS FOLLOW-BLACKOUT ON").HoldsSecret);
        Assert.False(CommandGate.Check("BLACKOUT ON").HoldsSecret);
    }

    [Fact]
    public void TheGateKeepsTheLineAndDropsARequestId()
    {
        var check = CommandGate.Check("@a1 LINK PATTERNS 10.0.0.5:9697");

        Assert.True(check.Ok, check.Reason);
        Assert.Equal("LINK PATTERNS 10.0.0.5:9697", check.Line);
        Assert.Null(check.Parsed!.RequestId);
    }

    [Fact]
    public void EveryVerbsSyntaxYieldsItsRequiredWords()
    {
        foreach (var v in VerbCatalog.All)
        {
            var tokens = CommandGate.RequiredTokens(v);
            Assert.All(tokens, t => Assert.False(t.StartsWith('['), $"{v.Name}: {t}"));
        }
        Assert.Equal(["<output>", "<column>", "<row>", "<dx>", "<dy>"], CommandGate.RequiredTokens(VerbCatalog.Find("WARP NODE")!));
        Assert.Equal(["<83|83.5|2.5s|1:23|1:02:03|top|END [s]>"], CommandGate.RequiredTokens(VerbCatalog.Find("VIDEO SEEK")!));
        Assert.Empty(CommandGate.RequiredTokens(VerbCatalog.Find("VIDEO END")!));
        Assert.Equal(["<camera>"], CommandGate.RequiredTokens(VerbCatalog.Find("SCAN START")!));
    }

    [Fact]
    public void OnlyIdsOfTheGeneratedShapeAreCheckedAgainstTheProject()
    {
        var project = SampleShow.Project();

        Assert.True(CommandGate.LooksLikeGeneratedId("sf00000001"));
        Assert.False(CommandGate.LooksLikeGeneratedId("op_1"));
        Assert.False(CommandGate.LooksLikeGeneratedId("sfZZZZZZZZ"));
        Assert.True(CommandGate.Check("OUTPUT ENABLE op_9 ON", project).Ok);
        Assert.False(CommandGate.Check("OUTPUT ENABLE op0000abcd ON", project).Ok);
        Assert.True(CommandGate.Check($"SURFACE SOURCE {SampleShow.Door} color:#FF0000", project).Ok);
    }

    [Fact]
    public void HighRiskNeverRunsWithoutAskingWhateverTheSettingsSay()
    {
        foreach (var low in new[] { false, true })
        {
            foreach (var review in new[] { false, true })
            {
                var s = AssistantSettings.Default with { RunLowWithoutAsking = low, RunReviewWithoutAsking = review };
                Assert.False(s.RunsWithoutAsking(VerbRisk.High));
                Assert.Equal(low, s.RunsWithoutAsking(VerbRisk.Low));
                Assert.Equal(review, s.RunsWithoutAsking(VerbRisk.Review));
            }
        }
        Assert.True(AssistantSettings.Default.RunsWithoutAsking(VerbRisk.Low));
        Assert.False(AssistantSettings.Default.RunsWithoutAsking(VerbRisk.Review));
        Assert.False(AssistantSettings.Default.Enabled);
        Assert.Equal("claude-opus-5-5", AssistantSettings.Default.Model);
        Assert.Equal(AssistantEffort.Medium, AssistantSettings.Default.Effort);
    }

    [Fact]
    public void SettingsOutOfRangeAreBroughtBackIntoIt()
    {
        var s = (AssistantSettings.Default with { MaxToolIterations = 0, MaxTokens = 10, Model = " ", RequestTimeout = TimeSpan.Zero }).Normalised();

        Assert.Equal(AssistantSettings.MinToolIterations, s.MaxToolIterations);
        Assert.Equal(AssistantSettings.MinMaxTokens, s.MaxTokens);
        Assert.Equal(AssistantSettings.DefaultClaudeModel, s.Model);
        Assert.True(s.RequestTimeout > TimeSpan.Zero);
    }

    [Fact]
    public void SecretsAreMaskedByShapeAndByName()
    {
        var r = new SecretRedactor(["hunter2-token", "abc"]);

        var masked = r.Redact("key sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUV, swk_live12345678, Bearer eyJhbGciOiJIUzI1, AUTH 0badc0de0badc0de, LINK STAGEWATCH 10.0.0.6 swkey12345678, hunter2-token, abc");

        Assert.DoesNotContain("sk-ant", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("swk_live", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("0badc0de", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("swkey1234", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", masked, StringComparison.Ordinal);
        Assert.EndsWith(", abc", masked, StringComparison.Ordinal);
        Assert.Contains("LINK STAGEWATCH 10.0.0.6 [secret]", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimeOfDayIsTheNextOneToComeOnTheNodesClock()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
        var now = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.FromHours(1));

        Assert.True(TimeWords.TryResolve("8pm", now, zone, out var eight, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.FromHours(1)), eight);
        Assert.True(TimeWords.TryResolve("8:15 PM", now, zone, out var quarter, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 20, 15, 0, TimeSpan.FromHours(1)), quarter);
        Assert.True(TimeWords.TryResolve("19:00", now, zone, out var tomorrow, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 19, 0, 0, TimeSpan.FromHours(1)), tomorrow);
        Assert.True(TimeWords.TryResolve("12am", now, zone, out var midnight, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(1)), midnight);
        Assert.True(TimeWords.TryResolve("+5m", now, zone, out var soon, out _));
        Assert.Equal(now.AddMinutes(5), soon);
        Assert.False(TimeWords.TryResolve("8", now, zone, out _, out var why));
        Assert.Contains("20:00", why, StringComparison.Ordinal);
        Assert.False(TimeWords.TryResolve("+5", now, zone, out _, out _));
        Assert.False(TimeWords.TryResolve("25:00", now, zone, out _, out _));
    }
}
