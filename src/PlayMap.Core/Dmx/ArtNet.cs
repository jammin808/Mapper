using System.Buffers.Binary;
using System.Text;

namespace PlayMap.Core.Dmx;

/// <summary>The Art-Net 4 operations PlayMap reads and writes (Art-Net 4 specification, "Art-Net packet definition").</summary>
public enum ArtOp : ushort
{
    Poll = 0x2000,
    PollReply = 0x2100,
    Dmx = 0x5000,
    Sync = 0x5200,
    TimeCode = 0x9700,
}

/// <summary>An ArtDmx packet's header fields; its data is the span after offset 18.</summary>
public readonly record struct ArtDmxHeader(byte Sequence, byte Physical, int PortAddress, int Length);

/// <summary>An ArtTimeCode packet: frames, seconds, minutes, hours, type (0 Film 24, 1 EBU 25, 2 DF 29.97, 3 SMPTE 30) and the
/// stream (Art-Net 4's StreamId; 0 = the master).</summary>
public readonly record struct ArtTimeCode(int Frames, int Seconds, int Minutes, int Hours, int Type, int StreamId);

/// <summary>What a node says in its ArtPollReply.</summary>
public sealed record ArtPollReplyInfo(byte[] Address, string ShortName, string LongName, string Report, IReadOnlyList<int> PortAddresses, int Oem);

/// <summary>Art-Net 4's wire format: every packet starts "Art-Net\0", then the OpCode low byte first; most then carry the protocol
/// version 14, high byte first. Readers refuse what is malformed (false, never a throw); writers fill a caller's buffer.</summary>
public static class ArtNet
{
    public const int Port = 0x1936;

    public const int ProtocolVersion = 14;

    public const int PollReplyLength = 239;

    public const int TimeCodeLength = 19;

    private static ReadOnlySpan<byte> Id => "Art-Net\0"u8;

    /// <summary>The OpCode of a packet that starts with the Art-Net id, or null.</summary>
    public static ArtOp? OpOf(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 10 || !packet[..8].SequenceEqual(Id))
            return null;
        return (ArtOp)BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]);
    }

    /// <summary>A packet carrying ProtVer: version 14 or later.</summary>
    private static bool Version(ReadOnlySpan<byte> packet) => packet.Length >= 12 && ((packet[10] << 8) | packet[11]) >= ProtocolVersion;

    public static bool TryReadDmx(ReadOnlySpan<byte> packet, out ArtDmxHeader header, out ReadOnlySpan<byte> data)
    {
        header = default;
        data = default;
        if (OpOf(packet) != ArtOp.Dmx || packet.Length < 20 || !Version(packet))
            return false;
        var length = (packet[16] << 8) | packet[17];
        if (length is < 2 or > 512 || packet.Length < 18 + length)
            return false;
        // Port-Address = Net (7 bits) << 8 | Sub-Net << 4 | Universe.
        header = new ArtDmxHeader(packet[12], packet[13], ((packet[15] & 0x7F) << 8) | packet[14], length);
        data = packet.Slice(18, length);
        return true;
    }

    public static bool TryReadTimeCode(ReadOnlySpan<byte> packet, out ArtTimeCode tc)
    {
        tc = default;
        if (OpOf(packet) != ArtOp.TimeCode || packet.Length < TimeCodeLength || !Version(packet))
            return false;
        var t = new ArtTimeCode(packet[14], packet[15], packet[16], packet[17], packet[18], packet[13]);
        if (t.Type is < 0 or > 3 || t.Hours > 23 || t.Minutes > 59 || t.Seconds > 59 || t.Frames > 29)
            return false;
        tc = t;
        return true;
    }

    public static bool IsPoll(ReadOnlySpan<byte> packet) => OpOf(packet) == ArtOp.Poll && packet.Length >= 14 && Version(packet);

    public static bool IsSync(ReadOnlySpan<byte> packet) => OpOf(packet) == ArtOp.Sync && packet.Length >= 14 && Version(packet);

    private static void Head(Span<byte> into, ArtOp op)
    {
        Id.CopyTo(into);
        BinaryPrimitives.WriteUInt16LittleEndian(into[8..], (ushort)op);
    }

    private static void Head(Span<byte> into, ArtOp op, bool version)
    {
        Head(into, op);
        if (version)
        {
            into[10] = 0;
            into[11] = ProtocolVersion;
        }
    }

    /// <summary>Writes an ArtDmx packet; answers its length.</summary>
    public static int WriteDmx(Span<byte> into, byte sequence, int portAddress, ReadOnlySpan<byte> data)
    {
        var length = Math.Clamp(data.Length + (data.Length & 1), 2, 512);
        Head(into, ArtOp.Dmx, true);
        into[12] = sequence;
        into[13] = 0;
        into[14] = (byte)(portAddress & 0xFF);
        into[15] = (byte)((portAddress >> 8) & 0x7F);
        into[16] = (byte)(length >> 8);
        into[17] = (byte)length;
        into.Slice(18, length).Clear();
        data[..Math.Min(data.Length, 512)].CopyTo(into[18..]);
        return 18 + length;
    }

    public static int WriteSync(Span<byte> into)
    {
        Head(into, ArtOp.Sync, true);
        into[12] = 0;
        into[13] = 0;
        return 14;
    }

    public static int WritePoll(Span<byte> into)
    {
        Head(into, ArtOp.Poll, true);
        into[12] = 0x02;
        into[13] = 0;
        return 14;
    }

    public static int WriteTimeCode(Span<byte> into, ArtTimeCode tc)
    {
        Head(into, ArtOp.TimeCode, true);
        into[12] = 0;
        into[13] = (byte)tc.StreamId;
        into[14] = (byte)tc.Frames;
        into[15] = (byte)tc.Seconds;
        into[16] = (byte)tc.Minutes;
        into[17] = (byte)tc.Hours;
        into[18] = (byte)tc.Type;
        return TimeCodeLength;
    }

    /// <summary>Writes an ArtPollReply (239 bytes): up to four ports, each an output from Art-Net (the node receives them), from the
    /// first four bound universes; Status2 says 15-bit port-addresses.</summary>
    public static int WritePollReply(Span<byte> into, ArtPollReplyInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        into[..PollReplyLength].Clear();
        Head(into, ArtOp.PollReply);
        info.Address.AsSpan(0, 4).CopyTo(into[10..]);
        BinaryPrimitives.WriteUInt16LittleEndian(into[14..], Port);
        into[16] = 0;
        into[17] = 1;
        // One reply names one Net and one Sub-Net (NetSwitch, SubSwitch): its ports are the first four universes that share the
        // first one's; a port's own byte holds only the universe's low nibble.
        var first = info.PortAddresses.Count > 0 ? info.PortAddresses[0] : 0;
        var ports = info.PortAddresses.Where(p => (p & 0x7FF0) == (first & 0x7FF0)).Take(4).ToList();
        into[18] = (byte)((first >> 8) & 0x7F);
        into[19] = (byte)((first >> 4) & 0x0F);
        into[20] = (byte)(info.Oem >> 8);
        into[21] = (byte)info.Oem;
        Text(into.Slice(26, 18), info.ShortName);
        Text(into.Slice(44, 64), info.LongName);
        Text(into.Slice(108, 64), info.Report);
        into[173] = (byte)ports.Count;
        for (var i = 0; i < ports.Count; i++)
        {
            into[174 + i] = 0x80;
            into[182 + i] = 0x80;
            into[190 + i] = (byte)(ports[i] & 0x0F);
        }
        into[200] = 0x00;
        info.Address.AsSpan(0, 4).CopyTo(into[207..]);
        into[211] = 1;
        into[212] = 0x08;
        return PollReplyLength;
    }

    private static void Text(Span<byte> into, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, into.Length - 1)).CopyTo(into);
    }

    /// <summary>The ShortName of an ArtPollReply.</summary>
    public static string ShortNameOf(ReadOnlySpan<byte> reply) =>
        OpOf(reply) == ArtOp.PollReply && reply.Length >= PollReplyLength ? Encoding.ASCII.GetString(reply.Slice(26, 18)).TrimEnd('\0') : "";
}
