using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PlayMap.Core.Dmx;

/// <summary>What an E1.31 packet is: DMX data (a data packet with the null START code), another START code's data (read, not
/// used), a synchronization packet, a universe discovery packet, or not E1.31.</summary>
public enum SacnKind { None, Data, OtherStartCode, Sync, Discovery }

/// <summary>A data packet's framing fields (ANSI E1.31-2018 §6.2) and its slot count; the slots follow the START code.</summary>
/// <param name="Priority">0–200 (100 by default).</param><param name="SyncAddress">0 = not synchronized.</param>
/// <param name="Preview">Options bit 7: data for visualisation only.</param><param name="Terminated">Options bit 6: the source stops.</param>
/// <param name="ForceSync">Options bit 5.</param><param name="Universe">1–63999.</param>
public readonly record struct SacnData(Guid Cid, string SourceName, int Priority, int SyncAddress, byte Sequence, bool Preview, bool Terminated,
    bool ForceSync, int Universe, byte StartCode, int Slots);

/// <summary>A synchronization packet (E1.31 §6.3): its source, sequence and synchronization address.</summary>
public readonly record struct SacnSync(Guid Cid, byte Sequence, int SyncAddress);

/// <summary>ANSI E1.31 (streaming ACN, "sACN") as read from the standard's tables: the root layer (preamble 0x0010, the ACN packet
/// identifier "ASC-E1.17", the CID), the framing layer (data, or the extended synchronization and discovery packets) and the DMP
/// layer (one SET PROPERTY of the START code and up to 512 slots). Every flags-and-length field is 0x7 and the length to the packet's
/// end. Readers refuse what is malformed (false, never a throw); writers fill a caller's buffer. UDP 5568; universe u is multicast
/// to 239.255.(u ÷ 256).(u mod 256).</summary>
public static class Sacn
{
    public const int Port = 5568;

    public const int MaxUniverse = 63999;

    /// <summary>A data packet's header length: the slots start after the START code at 126.</summary>
    public const int DataHeaderLength = 126;

    public const int SyncLength = 49;

    private const uint RootData = 0x00000004;
    private const uint RootExtended = 0x00000008;
    private const uint FramingData = 0x00000002;
    private const uint FramingSync = 0x00000001;
    private const uint FramingDiscovery = 0x00000002;

    private static ReadOnlySpan<byte> AcnId => "ASC-E1.17\0\0\0"u8;

    /// <summary>The multicast group of a universe.</summary>
    public static IPAddress GroupOf(int universe) =>
        new([239, 255, (byte)(universe >> 8), (byte)universe]);

    private static bool FlagsLength(ReadOnlySpan<byte> p, int at) =>
        p.Length >= at + 2 && (p[at] >> 4) == 0x7 && (((p[at] & 0x0F) << 8) | p[at + 1]) == p.Length - at;

    /// <summary>What a packet is, by its root and framing vectors (nothing else is checked).</summary>
    public static SacnKind KindOf(ReadOnlySpan<byte> p)
    {
        if (p.Length < 44 || BinaryPrimitives.ReadUInt16BigEndian(p) != 0x0010 || BinaryPrimitives.ReadUInt16BigEndian(p[2..]) != 0 || !p.Slice(4, 12).SequenceEqual(AcnId))
            return SacnKind.None;
        var root = BinaryPrimitives.ReadUInt32BigEndian(p[18..]);
        var framing = BinaryPrimitives.ReadUInt32BigEndian(p[40..]);
        return root switch
        {
            RootData when framing == FramingData => p.Length > 125 && p[125] != 0 ? SacnKind.OtherStartCode : SacnKind.Data,
            RootExtended when framing == FramingSync => SacnKind.Sync,
            RootExtended when framing == FramingDiscovery => SacnKind.Discovery,
            _ => SacnKind.None,
        };
    }

    /// <summary>Reads a data packet: every layer's flags and length, the DMP fields (vector 0x02, type 0xa1, first address 0,
    /// increment 1), a property count of 1–513 that matches the packet, a priority of at most 200 and a universe of 1–63999.</summary>
    public static bool TryReadData(ReadOnlySpan<byte> p, out SacnData data, out ReadOnlySpan<byte> slots)
    {
        data = default;
        slots = default;
        if (KindOf(p) is not (SacnKind.Data or SacnKind.OtherStartCode) || p.Length < DataHeaderLength || p.Length > DataHeaderLength + 512)
            return false;
        if (!FlagsLength(p, 16) || !FlagsLength(p, 38) || !FlagsLength(p, 115))
            return false;
        if (p[117] != 0x02 || p[118] != 0xA1 || BinaryPrimitives.ReadUInt16BigEndian(p[119..]) != 0 || BinaryPrimitives.ReadUInt16BigEndian(p[121..]) != 1)
            return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(p[123..]);
        if (count is < 1 or > 513 || count != p.Length - 125)
            return false;
        var priority = p[108];
        var universe = BinaryPrimitives.ReadUInt16BigEndian(p[113..]);
        if (priority > 200 || universe is < 1 or > MaxUniverse)
            return false;
        var name = p.Slice(44, 64);
        var end = name.IndexOf((byte)0);
        var options = p[112];
        data = new SacnData(new Guid(p.Slice(22, 16), bigEndian: true), Encoding.UTF8.GetString(end < 0 ? name : name[..end]), priority,
            BinaryPrimitives.ReadUInt16BigEndian(p[109..]), p[111], (options & 0x80) != 0, (options & 0x40) != 0, (options & 0x20) != 0,
            universe, p[125], count - 1);
        slots = p.Slice(DataHeaderLength, count - 1);
        return true;
    }

    /// <summary>Reads a synchronization packet (49 bytes; a synchronization address of 1–63999).</summary>
    public static bool TryReadSync(ReadOnlySpan<byte> p, out SacnSync sync)
    {
        sync = default;
        if (KindOf(p) != SacnKind.Sync || p.Length != SyncLength || !FlagsLength(p, 16) || !FlagsLength(p, 38))
            return false;
        var address = BinaryPrimitives.ReadUInt16BigEndian(p[45..]);
        if (address is < 1 or > MaxUniverse)
            return false;
        sync = new SacnSync(new Guid(p.Slice(22, 16), bigEndian: true), p[44], address);
        return true;
    }

    private static void Root(Span<byte> o, int length, uint vector, Guid cid)
    {
        BinaryPrimitives.WriteUInt16BigEndian(o, 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(o[2..], 0);
        AcnId.CopyTo(o[4..]);
        WriteFlagsLength(o[16..], length - 16);
        BinaryPrimitives.WriteUInt32BigEndian(o[18..], vector);
        cid.TryWriteBytes(o.Slice(22, 16), bigEndian: true, out _);
    }

    private static void WriteFlagsLength(Span<byte> o, int length) => BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)(0x7000 | (length & 0x0FFF)));

    /// <summary>Writes a data packet of a universe's slots (at most 512); answers its length.</summary>
    public static int WriteData(Span<byte> into, SacnData header, ReadOnlySpan<byte> slots)
    {
        if (slots.Length > 512)
            throw new ArgumentException("a universe carries at most 512 slots", nameof(slots));
        var n = DataHeaderLength + slots.Length;
        var o = into[..n];
        o.Clear();
        Root(o, n, RootData, header.Cid);
        WriteFlagsLength(o[38..], n - 38);
        BinaryPrimitives.WriteUInt32BigEndian(o[40..], FramingData);
        var name = Encoding.UTF8.GetBytes(header.SourceName);
        name.AsSpan(0, Math.Min(63, name.Length)).CopyTo(o[44..]);
        o[108] = (byte)header.Priority;
        BinaryPrimitives.WriteUInt16BigEndian(o[109..], (ushort)header.SyncAddress);
        o[111] = header.Sequence;
        o[112] = (byte)((header.Preview ? 0x80 : 0) | (header.Terminated ? 0x40 : 0) | (header.ForceSync ? 0x20 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(o[113..], (ushort)header.Universe);
        WriteFlagsLength(o[115..], n - 115);
        o[117] = 0x02;
        o[118] = 0xA1;
        BinaryPrimitives.WriteUInt16BigEndian(o[119..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(o[121..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(o[123..], (ushort)(slots.Length + 1));
        o[125] = header.StartCode;
        slots.CopyTo(o[DataHeaderLength..]);
        return n;
    }

    /// <summary>Writes a synchronization packet; answers its length (49).</summary>
    public static int WriteSync(Span<byte> into, SacnSync sync)
    {
        var o = into[..SyncLength];
        o.Clear();
        Root(o, SyncLength, RootExtended, sync.Cid);
        WriteFlagsLength(o[38..], SyncLength - 38);
        BinaryPrimitives.WriteUInt32BigEndian(o[40..], FramingSync);
        o[44] = sync.Sequence;
        BinaryPrimitives.WriteUInt16BigEndian(o[45..], (ushort)sync.SyncAddress);
        return SyncLength;
    }
}
