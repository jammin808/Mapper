using System.Buffers.Binary;
using System.Text;
using PlayMap.Core.Model;
using PlayMap.Media.Video.Spout;

namespace PlayMap.Media.Tests.Video.Spout;

/// <summary>Spout 2.007's protocol against a fake kernel, as goldens: the 280-byte info map, the sender list, names; a sender
/// registering, refused when the name is live, publishing (the frame counter net +1, the sync event set, the data map's facts),
/// skipping a frame a receiver holds; a receiver in Spout's order (access lock, then the counter, then the copy, the count committed
/// only after it), including the busy-mutex case (the count moved while the mutex was held: the next step copies frame N), a failed
/// copy, a read-back still in flight (never waited for), a keyed texture, an abandoned lock, a new handle, and a sender that left.</summary>
public sealed class SpoutProtocolTests
{
    private const string Name = "PlayMap stage - Main";
    private const int W = 4;
    private const int H = 2;

    private static byte[] Frame(byte value)
    {
        var f = new byte[W * H * 4];
        Array.Fill(f, value);
        return f;
    }

    private static (FakeSpoutKernel Kernel, FakeSpoutGpu Gpu, SpoutSender Sender) Registered(SpoutDataWriter? data = null)
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        var sender = new SpoutSender(kernel, gpu, Name, SpoutFormats.Bgra8, data);
        sender.Register(W, H);
        return (kernel, gpu, sender);
    }

    [Fact]
    public void TheInfoMapIs280BytesLaidOutAsSpoutDoes()
    {
        var bytes = new byte[SpoutInfo.Size];
        new SpoutInfo(0x40000040, 1920, 1080, 87, 0, @"C:\Shows\PlayMap.exe", 0x40000000).Write(bytes);
        Assert.Equal(0x40000040u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(87u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(@"C:\Shows\PlayMap.exe", Encoding.ASCII.GetString(bytes, 20, 20));
        Assert.Equal(0, bytes[40]);
        Assert.Equal(0x40000000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(276)));
        Assert.Equal(new SpoutInfo(0x40000040, 1920, 1080, 87, 0, @"C:\Shows\PlayMap.exe", 0x40000000), SpoutInfo.Read(bytes));
        // A description longer than its slot keeps its NUL.
        new SpoutInfo(1, 1, 1, 87, 0, new string('x', 400), 0).Write(bytes);
        Assert.Equal(255, SpoutInfo.Read(bytes).Description.Length);
        Assert.Equal(0, bytes[275]);
    }

    [Fact]
    public void TheSenderListIsSortedNulTerminatedSlotsEndedByAnEmptyOne()
    {
        var map = new byte[SpoutNames.MaxSenders * SpoutNames.NameBytes];
        SpoutNames.Write(map, ["Zeta", "Arena", "Arena", "Mid"]);
        Assert.Equal("Arena", Encoding.ASCII.GetString(map, 0, 5));
        Assert.Equal(0, map[5]);
        Assert.Equal("Mid", Encoding.ASCII.GetString(map, 256, 3));
        Assert.Equal("Zeta", Encoding.ASCII.GetString(map, 512, 4));
        Assert.Equal(0, map[768]);
        Assert.Equal(["Arena", "Mid", "Zeta"], SpoutNames.Read(map));
        Assert.Empty(SpoutNames.Read(new byte[512]));

        Assert.Equal("", SpoutNames.Invalid("Resolume Arena"));
        Assert.Equal("a Spout name is printable ASCII only: 'Bühne' holds 'ü'", SpoutNames.Invalid("Bühne"));
        Assert.Equal("a Spout name is at most 255 characters (256 given)", SpoutNames.Invalid(new string('a', 256)));
        Assert.Equal("a Spout name is 1–255 printable ASCII characters", SpoutNames.Invalid(""));
        Assert.Equal("PlayMap B?hne - Main", SpoutNames.Default("Bühne", "Main"));
        Assert.Equal("Arena_SpoutAccessMutex", SpoutNames.AccessMutex("Arena"));
        Assert.Equal("Arena_Count_Semaphore", SpoutNames.Counter("Arena"));
        Assert.Equal("Arena_Sync_Event", SpoutNames.SyncEvent("Arena"));
        Assert.Equal("Arena_map", SpoutNames.DataMap("Arena"));
        Assert.Equal("Arena_mutex", SpoutNames.MutexOf("Arena"));
    }

    [Fact]
    public void ASenderRegistersItsNameTextureAndObjectsAndANameTakenIsRefused()
    {
        var (kernel, gpu, sender) = Registered();
        using (sender)
        {
            Assert.Equal([Name], SpoutNames.Read(kernel.MapBytes(SpoutNames.List)));
            Assert.Equal([Name], SpoutNames.Read(kernel.MapBytes(SpoutNames.Active)));
            var info = SpoutInfo.Read(kernel.MapBytes(Name));
            Assert.Equal(sender.Texture!.Handle, info.ShareHandle);
            Assert.Equal(((uint)W, (uint)H, 87u, 0u, 0u), (info.Width, info.Height, info.Format, info.Usage, info.PartnerId));
            Assert.Equal(@"C:\Shows\PlayMap.exe", info.Description);
            Assert.Equal(1, kernel.Count(SpoutNames.Counter(Name)));
            foreach (var o in new[] { SpoutNames.AccessMutex(Name), SpoutNames.SyncEvent(Name), SpoutNames.DataMap(Name), SpoutNames.MutexOf(Name) })
                Assert.True(kernel.Exists(o), o);
            // The list was changed under its mutex, waited for as Spout waits (67 ms), off the frame path.
            Assert.Contains("try SpoutSenderNames_mutex 67", kernel.Log);

            var twin = new SpoutSender(kernel, gpu, Name, SpoutFormats.Bgra8);
            var ex = Assert.Throws<InvalidOperationException>(() => twin.Register(W, H));
            Assert.Equal(@"a Spout sender named 'PlayMap stage - Main' already exists: C:\Shows\PlayMap.exe", ex.Message);
            Assert.Equal([Name], SpoutNames.Read(kernel.MapBytes(SpoutNames.List)));

            // A second sender: listed, the first stays active.
            using var second = new SpoutSender(kernel, gpu, "Arena", SpoutFormats.Bgra8);
            second.Register(W, H);
            Assert.Equal(["Arena", Name], SpoutNames.Read(kernel.MapBytes(SpoutNames.List)));
            Assert.Equal([Name], SpoutNames.Read(kernel.MapBytes(SpoutNames.Active)));
        }
        // Gone: unlisted, and its info says no texture to a receiver that still holds it.
        Assert.Null(kernel.MapBytes(Name));
    }

    [Fact]
    public void ANameSpoutCannotCarryOrMemoryShareIsRefusedWithWords()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        Assert.Equal("a Spout name is printable ASCII only: 'Bühne' holds 'ü'",
            Assert.Throws<InvalidOperationException>(() => new SpoutSender(kernel, gpu, "Bühne", 87).Register(W, H)).Message);
        kernel.MemoryShare = true;
        Assert.Equal("this PC's Spout is set to 2.006 memory share (SpoutSettings); PlayMap shares GPU textures only",
            Assert.Throws<InvalidOperationException>(() => new SpoutSender(kernel, gpu, "Main", 87).Register(W, H)).Message);
        Assert.Null(kernel.MapBytes(SpoutNames.List));
    }

    [Fact]
    public void PublishingCountsNetOneTellsReceiversAndWritesTheFacts()
    {
        var data = new SpoutDataWriter("a1b2c3d4", "sd_main", "output:op_1", "60000/1001");
        var (kernel, gpu, sender) = Registered(data);
        using (sender)
        {
            kernel.Log.Clear();
            Assert.True(sender.Publish(Frame(7), W * 4, leaderNs: 1_760_360_000_123_456_789));
            Assert.Equal(Frame(7), gpu.Sender(sender.Texture!.Handle)!.Rows);
            Assert.Equal(2, kernel.Count(SpoutNames.Counter(Name)));
            Assert.Equal(1, kernel.Sets(SpoutNames.SyncEvent(Name)));
            // Spout's sender order: the access mutex at 0 ms, the picture, the counter (take one, give two), release, the event.
            Assert.Equal(
                [
                    $"try {Name}_SpoutAccessMutex 0", $"take {Name}_Count_Semaphore", $"give {Name}_Count_Semaphore 2",
                    $"release {Name}_SpoutAccessMutex", $"set {Name}_Sync_Event", $"try {Name}_map_mutex 0", $"release {Name}_map_mutex",
                ],
                kernel.Log);
            Assert.Equal("""{"pm":1,"node":"a1b2c3d4","send":"sd_main","rate":"60000/1001","tap":"output:op_1","frame":1,"leaderNs":1760360000123456789,"w":4,"h":2}""",
                SpoutDataWriter.Read(kernel.MapBytes(SpoutNames.DataMap(Name))));
            Assert.True(sender.Publish(Frame(8), W * 4));
            Assert.Equal(3, kernel.Count(SpoutNames.Counter(Name)));
            Assert.Equal(2, sender.Frames);

            // A receiver holds the texture: the frame is skipped and counted, never waited for; the counter does not move.
            Assert.True(kernel.HoldByAnotherApp(SpoutNames.AccessMutex(Name)));
            Assert.False(sender.Publish(Frame(9), W * 4));
            Assert.Equal(1, sender.Busy);
            Assert.Equal(3, kernel.Count(SpoutNames.Counter(Name)));
            Assert.Equal(Frame(8), gpu.Sender(sender.Texture.Handle)!.Rows);
            kernel.ReleaseByAnotherApp(SpoutNames.AccessMutex(Name));

            // A receiver that died holding it: taken, said once.
            kernel.AbandonByAnotherApp(SpoutNames.AccessMutex(Name));
            Assert.True(sender.Publish(Frame(10), W * 4));
            Assert.True(sender.SawAbandoned);
        }
    }

    [Fact]
    public void AReceiverCopiesInSpoutsOrderAndTheBusyMutexCaseCopiesFrameN()
    {
        var (kernel, gpu, sender) = Registered();
        using (sender)
        using (var receiver = new SpoutReceiver(kernel, gpu, Name))
        {
            sender.Publish(Frame(1), W * 4);
            kernel.Log.Clear();
            Assert.Equal(ReceiveStep.Copied, receiver.Step());
            Assert.Equal(
                [
                    $"open map {Name}", $"open mutex {Name}_mutex", $"open mutex {Name}_SpoutAccessMutex", $"open semaphore {Name}_Count_Semaphore",
                    $"open event {Name}_Sync_Event", $"try {Name}_mutex 0", $"release {Name}_mutex",
                    $"try {Name}_SpoutAccessMutex 0", $"take {Name}_Count_Semaphore", $"give {Name}_Count_Semaphore 1",
                    $"release {Name}_SpoutAccessMutex", $"set {Name}_Sync_Event",
                ],
                kernel.Log);
            // The count before giving back: the sender's frames since the semaphore was made (from 1, net one a frame, one taken).
            Assert.Equal(1, receiver.LastCount);
            Assert.Equal(2, kernel.Count(SpoutNames.Counter(Name)));
            var picture = new byte[W * H * 4];
            Assert.Equal(StagingRead.Ready, receiver.ReadPending(picture, W * 4));
            Assert.Equal(Frame(1), picture);

            // Nothing new: the counter read again gives the same count.
            Assert.Equal(ReceiveStep.NotNew, receiver.Step());
            Assert.False(receiver.HasPending);

            // The sender draws frame 2, then another receiver holds the access mutex while it is our turn.
            sender.Publish(Frame(2), W * 4);
            Assert.True(kernel.HoldByAnotherApp(SpoutNames.AccessMutex(Name)));
            Assert.Equal(ReceiveStep.Busy, receiver.Step());
            Assert.Equal(1, receiver.Busy);
            Assert.Equal(1, receiver.LastCount);
            Assert.Equal(3, kernel.Count(SpoutNames.Counter(Name)));
            kernel.ReleaseByAnotherApp(SpoutNames.AccessMutex(Name));
            // The next step copies frame 2: the count was not touched while the mutex was busy.
            Assert.Equal(ReceiveStep.Copied, receiver.Step());
            Assert.Equal(2, receiver.LastCount);
            Assert.Equal(StagingRead.Ready, receiver.ReadPending(picture, W * 4));
            Assert.Equal(Frame(2), picture);
        }
    }

    [Fact]
    public void AReceiverThatFindsTheCounterTakenGivesNothingBack()
    {
        var (kernel, gpu, sender) = Registered();
        using (sender)
        using (var receiver = new SpoutReceiver(kernel, gpu, Name))
        {
            sender.Publish(Frame(1), W * 4);
            Assert.Equal(ReceiveStep.Copied, receiver.Step());
            // Another receiver holds the count between its take and its give: this one's take fails and it must not add one.
            using var other = kernel.OpenSemaphore(SpoutNames.Counter(Name))!;
            Assert.True(other.TryTake(0));
            Assert.True(other.TryTake(0));
            Assert.Equal(0, kernel.Count(SpoutNames.Counter(Name)));
            _ = receiver.Step();
            Assert.Equal(0, kernel.Count(SpoutNames.Counter(Name)));
            Assert.Equal(0, other.Release(2));
        }
    }

    [Fact]
    public void AFailedCopyLeavesTheCountAndAReadBackInFlightIsNeverWaitedFor()
    {
        var (kernel, gpu, sender) = Registered();
        using (sender)
        using (var receiver = new SpoutReceiver(kernel, gpu, Name))
        {
            sender.Publish(Frame(3), W * 4);
            gpu.FailNextCopy = true;
            Assert.Equal(ReceiveStep.Failed, receiver.Step());
            Assert.Equal(-1, receiver.LastCount);
            Assert.Contains("the device was removed", receiver.Words, StringComparison.Ordinal);
            // The lock was released all the same: the sender publishes again.
            Assert.True(sender.Publish(Frame(4), W * 4));

            gpu.PendingReads = 2;
            Assert.Equal(ReceiveStep.Copied, receiver.Step());
            var picture = new byte[W * H * 4];
            Assert.Equal(StagingRead.Pending, receiver.ReadPending(picture, W * 4));
            Assert.Equal(StagingRead.Pending, receiver.ReadPending(picture, W * 4));
            Assert.True(receiver.HasPending);
            Assert.Equal(StagingRead.Ready, receiver.ReadPending(picture, W * 4));
            Assert.Equal(Frame(4), picture);
            Assert.False(receiver.HasPending);
        }
    }

    [Fact]
    public void AKeyedTextureIsLockedByItsKeyAndANewHandleIsOpenedAgain()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu { KeyedNext = true };
        using var sender = new SpoutSender(kernel, gpu, Name, SpoutFormats.Bgra8);
        sender.Register(W, H);
        using var receiver = new SpoutReceiver(kernel, gpu, Name);
        sender.Publish(Frame(5), W * 4);
        kernel.Log.Clear();
        Assert.Equal(ReceiveStep.Copied, receiver.Step());
        // The keyed mutex is the lock: the access mutex is not tried.
        Assert.DoesNotContain($"try {Name}_SpoutAccessMutex 0", kernel.Log);
        var key = gpu.Sender(sender.Texture!.Handle)!;
        key.KeyHolder = "another app";
        Assert.Equal(ReceiveStep.Busy, receiver.Step());
        key.KeyHolder = null;

        // A new size: a new texture and handle under the same name; the receiver opens it at its next step.
        sender.Resize(W * 2, H);
        var opens = gpu.Opens;
        Assert.Equal(ReceiveStep.Copied, receiver.Step());
        Assert.Equal(opens + 1, gpu.Opens);
        Assert.Equal(W * 2, receiver.Width);
    }

    [Fact]
    public void ASenderThatLeftOrNeverWasIsAbsentWithWords()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        using (var nobody = new SpoutReceiver(kernel, gpu, "Arena"))
        {
            Assert.Equal(ReceiveStep.Absent, nobody.Step());
            Assert.Equal("no Spout sender named 'Arena'", nobody.Words);
        }
        var sender = new SpoutSender(kernel, gpu, Name, SpoutFormats.Bgra8);
        sender.Register(W, H);
        using var receiver = new SpoutReceiver(kernel, gpu, Name);
        sender.Publish(Frame(6), W * 4);
        Assert.Equal(ReceiveStep.Copied, receiver.Step());
        sender.Dispose();
        Assert.Equal(ReceiveStep.Absent, receiver.Step());
        Assert.Equal($"Spout sender '{Name}' shares no texture", receiver.Words);
        Assert.Empty(SpoutNames.Read(kernel.MapBytes(SpoutNames.List) ?? new byte[256]));
    }

    [Fact]
    public void DiscoveryReadsTheListAndEachSendersInfo()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        Assert.Empty(SpoutDirectory.Read(kernel));
        using var a = new SpoutSender(kernel, gpu, "Arena", 87);
        a.Register(W, H);
        using var b = new SpoutSender(kernel, gpu, Name, 87);
        b.Register(W, H);
        var seen = SpoutDirectory.Read(kernel);
        Assert.Equal(["Arena", Name], seen.Select(s => s.Name));
        Assert.True(seen[0].Active);
        Assert.False(seen[1].Active);
        Assert.Equal((uint)W, seen[1].Info!.Value.Width);
    }

    [Fact]
    public void PackedFramesConvertToBgra()
    {
        // UYVY: white, black, red (BT.709 limited) in pairs of one Cb Y Cr Y.
        byte[] uyvy = [128, 235, 128, 235, 128, 16, 128, 16, 102, 63, 240, 63, 128, 126, 128, 126];
        var bgra = new byte[8 * 4];
        Assert.True(SpoutPixels.ToBgra(uyvy, 16, SendFormat.Uyvy, 8, 1, bgra));
        Assert.Equal([255, 255, 255, 255], bgra[..4]);
        Assert.Equal([0, 0, 0, 255], bgra[8..12]);
        Assert.InRange(bgra[16 + 2], 250, 255);
        Assert.InRange(bgra[16 + 1], 0, 5);
        Assert.InRange(bgra[16], 0, 5);
        Assert.Equal(bgra[24], bgra[26]);
        Assert.False(SpoutPixels.ToBgra(uyvy, 16, SendFormat.Rgba16F, 8, 1, bgra));
        // BGRA passes through row by row (a padded stride dropped).
        byte[] padded = [1, 2, 3, 4, 9, 9, 5, 6, 7, 8, 9, 9];
        var out2 = new byte[8];
        Assert.True(SpoutPixels.ToBgra(padded, 6, SendFormat.Bgra8, 1, 2, out2));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], out2);
        // UYVA keeps its alpha plane.
        byte[] uyva = [128, 235, 128, 235, 7, 9];
        var out3 = new byte[8];
        Assert.True(SpoutPixels.ToBgra(uyva, 4, SendFormat.Uyva, 2, 1, out3));
        Assert.Equal(7, out3[3]);
        Assert.Equal(9, out3[7]);
    }
}
