using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlayMap.Core.Sync;

namespace PlayMap.Net.Sync;

/// <summary>One output's vblank facts, sent by a group member to its leader on the leader's clock-sync port (UDP 9942, beside the
/// PMCK exchange, told apart by the magic): 64 bytes, little-endian —
/// <code>
///  0  4  magic "PMVB"            32  8  i64 last vblank, leader ns (≥ 0)
///  4  1  version 1               40  8  u64 refresh index
///  5  1  lock state (0–7)        48  4  u32 vblanks missed in the last 60 s (≤ 60 000)
///  6  1  flags                   52  4  u32 house sync, milli-Hz (≤ 1 000 000)
///  7  1  path (0–2)              56  4  i32 air latency, µs (0 … 500 000)
///  8  4  u32 node id             60  4  u32 clock uncertainty, µs (≤ 1 000 000)
/// 12  4  u32 output id hash (FNV-1a 32 of the id's UTF-8)
/// 16  4  u32 sequence (per node and output)
/// 20  4  u32 board frame count (24 bits; the high byte zero)
/// 24  8  i64 refresh period, ps (1 000 000 000 … 50 000 000 000: 20–1000 Hz)
/// </code>
/// Flags: bit 0 flip evidence, 1 composed, 2 settled, 3 barrier bound, 4 house sync present, 5 armed, 6 board present, 7 board
/// synced (only with a board). The clock uncertainty is the sender's own leader-time uncertainty (half its best round trip to
/// the leader; 0 on the leader): the board adds it to every phase it publishes, so a 210 µs uncertainty is never shown as a
/// 12 µs phase. Anything else — another length, version, magic, a field out of range — is not a report: refused, never guessed
/// at (fail closed). Reports never change the air; they only inform the leader's numbers.</summary>
public readonly record struct VblankReport(LockState State, byte Flags, byte Path, uint NodeId, uint OutputIdHash, uint Seq,
    uint BoardFrameCount, long PeriodPs, long LastVblankLeaderNs, ulong RefreshIndex, uint Missed60s, uint HouseSyncMilliHz, int AirLatencyUs,
    uint ClockUncertaintyUs)
{
    public const int Size = 64;
    public const uint Magic = 0x42564D50;   // "PMVB" read little-endian
    public const byte Version = 1;
    public const long MinPeriodPs = 1_000_000_000;    // 1000 Hz
    public const long MaxPeriodPs = 50_000_000_000;   // 20 Hz
    public const int MaxAirLatencyUs = 500_000;
    public const uint MaxMissed60s = 60_000;
    public const uint MaxHouseSyncMilliHz = 1_000_000;
    public const uint MaxClockUncertaintyUs = 1_000_000;

    public const byte FlagFlipEvidence = 1;
    public const byte FlagComposed = 2;
    public const byte FlagSettled = 4;
    public const byte FlagBarrier = 8;
    public const byte FlagHouseSync = 16;
    public const byte FlagArmed = 32;
    public const byte FlagBoard = 64;
    public const byte FlagBoardSynced = 128;

    /// <summary>Path words: 0 offscreen, 1 gl, 2 dxgi-flip.</summary>
    public const byte PathOffscreen = 0;
    public const byte PathGl = 1;
    public const byte PathDxgiFlip = 2;

    public bool Settled => (Flags & FlagSettled) != 0;

    public string PathWord => Path switch
    {
        PathGl => "gl",
        PathDxgiFlip => "dxgi-flip",
        _ => "offscreen",
    };

    /// <summary>The node id as eight hex (how STATE names it).</summary>
    public string NodeText => NodeId.ToString("x8", CultureInfo.InvariantCulture);

    public double PeriodNs => PeriodPs / 1000.0;

    public void Write(Span<byte> b)
    {
        if (b.Length < Size)
            throw new ArgumentException("a vblank report is 64 bytes", nameof(b));
        b[..Size].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        b[4] = Version;
        b[5] = (byte)State;
        b[6] = Flags;
        b[7] = Path;
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], NodeId);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], OutputIdHash);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], BoardFrameCount);
        BinaryPrimitives.WriteInt64LittleEndian(b[24..], PeriodPs);
        BinaryPrimitives.WriteInt64LittleEndian(b[32..], LastVblankLeaderNs);
        BinaryPrimitives.WriteUInt64LittleEndian(b[40..], RefreshIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(b[48..], Missed60s);
        BinaryPrimitives.WriteUInt32LittleEndian(b[52..], HouseSyncMilliHz);
        BinaryPrimitives.WriteInt32LittleEndian(b[56..], AirLatencyUs);
        BinaryPrimitives.WriteUInt32LittleEndian(b[60..], ClockUncertaintyUs);
    }

    /// <summary>True when this report would read back as itself: every field in range.</summary>
    public bool IsValid =>
        (uint)State <= (uint)LockState.Joining && Path <= PathDxgiFlip && (BoardFrameCount & 0xFF000000) == 0
        && PeriodPs is >= MinPeriodPs and <= MaxPeriodPs && LastVblankLeaderNs >= 0 && Missed60s <= MaxMissed60s
        && HouseSyncMilliHz <= MaxHouseSyncMilliHz && AirLatencyUs is >= 0 and <= MaxAirLatencyUs && ClockUncertaintyUs <= MaxClockUncertaintyUs
        && ((Flags & FlagBoardSynced) == 0 || (Flags & FlagBoard) != 0);

    /// <summary>Starts with the magic: a datagram on the sync port that claims to be a report (and is judged as one).</summary>
    public static bool Claims(ReadOnlySpan<byte> b) => b.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(b) == Magic;

    public static bool TryRead(ReadOnlySpan<byte> b, out VblankReport report)
    {
        report = default;
        if (b.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic || b[4] != Version)
            return false;
        var r = new VblankReport((LockState)b[5], b[6], b[7],
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[52..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[56..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[60..]));
        if (!r.IsValid)
            return false;
        report = r;
        return true;
    }

    /// <summary>FNV-1a (32 bit) of an id's UTF-8: the output id hash every node computes the same way.</summary>
    public static uint HashId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var hash = 2166136261u;
        Span<byte> one = stackalloc byte[4];
        foreach (var rune in id.EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(one);
            for (var i = 0; i < n; i++)
                hash = unchecked((hash ^ one[i]) * 16777619u);
        }
        return hash;
    }

    /// <summary>A node id as the report carries it: its eight hex as a number, or (an id that is not eight hex) its FNV-1a hash.</summary>
    public static uint NodeNumber(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return nodeId.Length == 8 && uint.TryParse(nodeId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) ? n : HashId(nodeId);
    }
}

/// <summary>Where the clock-sync responder hands the reports it admitted (the leader's sync board): any thread.</summary>
public interface IVblankSink
{
    void Offer(in VblankReport report, System.Net.IPAddress from);
}

/// <summary>The sink each leader clock's responder hands its reports to. The responder is made by the network host with the
/// node's <see cref="Core.Time.NetworkClock"/>; the sync package registers its board against the same clock, so several nodes in
/// one process each keep their own.</summary>
public static class VblankSinks
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Core.Time.NetworkClock, IVblankSink> Sinks = [];

    public static void Attach(Core.Time.NetworkClock clock, IVblankSink sink)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sink);
        Sinks.AddOrUpdate(clock, sink);
    }

    public static void Detach(Core.Time.NetworkClock clock, IVblankSink sink)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Sinks.TryGetValue(clock, out var held) && ReferenceEquals(held, sink))
            Sinks.Remove(clock);
    }

    public static IVblankSink? For(Core.Time.NetworkClock clock) => Sinks.TryGetValue(clock, out var sink) ? sink : null;
}
