using PlayMap.Net.Fleet;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The fleet's pin file is the operator's fix for a reinstalled machine (preflight names it): a line removed by hand counts at
/// the next dial without a restart, and a pin the node sets afterwards never writes the removed line back.</summary>
public sealed class FleetPinFileTests
{
    private static readonly string Old = "sha256:" + new string('a', 64);
    private static readonly string Other = "sha256:" + new string('b', 64);
    private static readonly string Reinstalled = "sha256:" + new string('c', 64);

    [Fact]
    public void ALineRemovedByHandCountsWithoutARestartAndIsNeverWrittenBack()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-pins-");
        try
        {
            var path = Path.Combine(dir.FullName, "fleet-pins.json");
            var file = new FleetPinFile(path);
            var b = PinStore.NodeKey("9f8e7d6c");
            var c = PinStore.NodeKey("c0ffee00");
            file.Current().Pin(b, Old);
            file.Current().Pin(c, Other);
            Assert.Equal(PinVerdict.Changed, file.Current().Check(b, Reinstalled, Reinstalled));

            // The operator removes FOH-B's line: it was reinstalled.
            File.WriteAllText(path, "{\"" + c + "\":\"" + Other + "\"}");
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
            Assert.Null(file.Current().Get(b));
            Assert.Equal(PinVerdict.FirstUse, file.Current().Check(b, Reinstalled, Reinstalled));

            // Its next record pins the new certificate over the edited file: the old pin does not come back, the other line stays.
            file.Current().Pin(b, Reinstalled);
            var text = File.ReadAllText(path);
            Assert.Contains(Reinstalled, text, StringComparison.Ordinal);
            Assert.Contains(Other, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Old, text, StringComparison.Ordinal);
            Assert.Equal(PinVerdict.Pinned, new FleetPinFile(path).Current().Check(b, Reinstalled, Reinstalled));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ARefusedCertificateSaysWhereTheOperatorFixesIt()
    {
        var words = HealthFeedClient.RefusedWords(PinVerdict.Changed, "FOH-B", Reinstalled);
        Assert.StartsWith("CERTIFICATE CHANGED: FOH-B", words, StringComparison.Ordinal);
        Assert.Contains("fleet's pin file", words, StringComparison.Ordinal);
        Assert.DoesNotContain("Network drawer", words, StringComparison.Ordinal);
    }
}
