using PlayMap.Net.Phone;

namespace PlayMap.Net.Tests.Phone;

/// <summary>A phones record this account may not read (an ACL, a file another account made) never stops the keys from starting: they
/// start with no phones, and the record is never written over or moved over another. It is moved aside whole; where the name aside is
/// taken or the move is refused it stays where it is and a save that cannot move it first throws, writing nothing. The refused read is the read seam's
/// <see cref="UnauthorizedAccessException"/>, the exception the runtime raises for EACCES and for Windows' access denied: a file mode
/// cannot make it for a test run as root, which reads any file.</summary>
public sealed class PhoneKeysAccessTests
{
    private const string OtherAccountsRecord = """
        {
          "phones": [
            {
              "id": "ph-other",
              "name": "Other account's phone",
              "keySha256": "sha256:00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
              "createdUnixMs": 1000,
              "lastSeenUnixMs": 2000,
              "rig": "cam-other"
            }
          ]
        }
        """;

    private static PhoneRecord Phone(string id) =>
        new(id, "Phone " + id, PhoneKeys.Digest("key of " + id), 5000, 5000, "");

    private static Func<string, byte[]> Refused(List<string> reads) => path =>
    {
        reads.Add(path);
        throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
    };

    [Fact]
    public void ARecordThisAccountMayNotReadIsMovedAsideWholeAndTheKeysStartEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-phones-").FullName;
        try
        {
            var path = Path.Combine(dir, "phones.json");
            File.WriteAllText(path, OtherAccountsRecord);
            var bytes = File.ReadAllBytes(path);
            var reads = new List<string>();

            var keys = new PhoneKeys(path, Refused(reads));

            Assert.Equal([path], reads);
            Assert.Empty(keys.All);
            Assert.False(File.Exists(path));
            Assert.Equal(bytes, File.ReadAllBytes(path + ".unreadable"));

            // The next save writes this node's own record and leaves the one moved aside as it was.
            keys.Put(Phone("ph-new"));
            Assert.Equal(["ph-new"], new PhoneKeys(path).All.Select(p => p.Id));
            Assert.Equal(bytes, File.ReadAllBytes(path + ".unreadable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARecordThisAccountMayNotReadWhoseMoveIsRefusedIsNeverSavedOver()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-phones-").FullName;
        try
        {
            var path = Path.Combine(dir, "phones.json");
            File.WriteAllText(path, OtherAccountsRecord);
            var bytes = File.ReadAllBytes(path);
            // A folder where ".unreadable" would go takes the name aside: the record is not moved.
            var inTheWay = Path.Combine(path + ".unreadable", "in the way");
            Directory.CreateDirectory(inTheWay);

            var keys = new PhoneKeys(path, Refused([]));

            Assert.Empty(keys.All);
            Assert.Equal(bytes, File.ReadAllBytes(path));

            // A save while the record cannot be moved aside throws and writes nothing: not over the record, not beside it.
            var refused = Record.Exception(() => keys.Put(Phone("ph-new")));
            Assert.True(refused is IOException or UnauthorizedAccessException, $"the save was not refused: {refused}");
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));

            // Once the way is clear the next save moves the record aside whole first, then writes this node's own.
            Directory.Delete(path + ".unreadable", recursive: true);
            keys.Put(Phone("ph-next"));
            Assert.Equal(bytes, File.ReadAllBytes(path + ".unreadable"));
            var saved = new PhoneKeys(path).All.Select(p => p.Id).ToList();
            Assert.Contains("ph-next", saved);
            Assert.DoesNotContain("ph-other", saved);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>An earlier ".unreadable" may be another account's record (two accounts taking turns on one node folder): a record this
    /// account may not read is not moved over it, and no save is made over either.</summary>
    [Fact]
    public void ARecordThisAccountMayNotReadIsNeverMovedOverAnEarlierOneAside()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-phones-").FullName;
        try
        {
            var path = Path.Combine(dir, "phones.json");
            File.WriteAllText(path + ".unreadable", "an earlier start's record of another account");
            File.WriteAllText(path, OtherAccountsRecord);
            var aside = File.ReadAllBytes(path + ".unreadable");
            var bytes = File.ReadAllBytes(path);

            var keys = new PhoneKeys(path, Refused([]));

            Assert.Empty(keys.All);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(aside, File.ReadAllBytes(path + ".unreadable"));

            var refused = Record.Exception(() => keys.Put(Phone("ph-new")));
            Assert.IsType<IOException>(refused);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(aside, File.ReadAllBytes(path + ".unreadable"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The other side of the rule: a record that does not parse holds nothing to keep, so where its move aside is refused the
    /// next save replaces it.</summary>
    [Fact]
    public void ARecordThatDoesNotParseWhoseMoveIsRefusedIsReplacedByTheNextSave()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-phones-").FullName;
        try
        {
            var path = Path.Combine(dir, "phones.json");
            File.WriteAllText(path, "{ not json");
            Directory.CreateDirectory(Path.Combine(path + ".unreadable", "in the way"));

            var keys = new PhoneKeys(path);
            Assert.Empty(keys.All);

            keys.Put(Phone("ph-new"));
            Assert.Equal(["ph-new"], new PhoneKeys(path).All.Select(p => p.Id));
            Assert.True(Directory.Exists(path + ".unreadable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
