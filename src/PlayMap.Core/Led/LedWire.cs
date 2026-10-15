using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using PlayMap.Core.Dmx;

namespace PlayMap.Core.Led;

/// <summary>What an Art-Net node says about itself in its ArtPollReply, as LED output reads it.</summary>
/// <param name="Address">The node's IPv4 address (bytes 10–13).</param><param name="Port">Bytes 14–15 (little-endian; 0x1936).</param>
/// <param name="Universes">The Port-Addresses its output ports listen to (NetSwitch, SubSwitch and SwOut of each output port).</param>
/// <param name="RefreshHz">The rate it accepts (RefreshRate, bytes 226–227 big-endian; 0–44 reads as 44).</param>
public sealed record ArtNodeReply(string Address, int Port, string ShortName, string LongName, ImmutableArray<int> Universes, string Mac,
    int RefreshHz, int Oem, int EstaMan);

/// <summary>The packets LED output adds to Art-Net 4 (ArtDmx and ArtSync are <see cref="ArtNet"/>'s): the 22-byte ArtPoll with
/// EstaMan and Oem, and a lenient, length-checked ArtPollReply reader. Offsets are the Art-Net 4 specification's.</summary>
public static class LedArtNet
{
    /// <summary>The 22-byte ArtPoll: Flags bit 1 (reply on change), the target range 0–32767, EstaMan and Oem.</summary>
    public const int PollLength = 22;

    /// <summary>The ArtPollReply fields LED reads end at RefreshRate (226–227); anything shorter than its fixed part is refused.</summary>
    public const int ReplyMinimum = 207;

    /// <summary>The ESTA manufacturer code PlayMap sends (0x7FF0 is ESTA's prototype range, for products without an assignment).</summary>
    public const ushort EstaMan = 0x7FF0;

    /// <summary>The Oem code PlayMap sends (0x00FF: Art-Net's "unknown/non-registered").</summary>
    public const ushort Oem = 0x00FF;

    /// <summary>Writes the 22-byte ArtPoll; answers its length.</summary>
    public static int WritePoll(Span<byte> into)
    {
        var o = into[..PollLength];
        o.Clear();
        "Art-Net\0"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o[8..], (ushort)ArtOp.Poll);
        o[10] = 0;
        o[11] = ArtNet.ProtocolVersion;
        o[12] = 0x02;
        o[13] = 0;
        // TargetPortAddressTop 32767 (Hi, Lo) and Bottom 0: every universe.
        o[14] = 0x7F;
        o[15] = 0xFF;
        o[16] = 0;
        o[17] = 0;
        o[18] = EstaMan >> 8;
        o[19] = EstaMan & 0xFF;
        o[20] = Oem >> 8;
        o[21] = Oem & 0xFF;
        return PollLength;
    }

    /// <summary>Reads an ArtPollReply leniently: every field is length-checked, an absent RefreshRate reads as 44, bytes past the
    /// ones LED uses are ignored. False with the reason for a packet that is not one.</summary>
    public static bool TryReadReply(ReadOnlySpan<byte> p, out ArtNodeReply reply, out string why)
    {
        reply = null!;
        if (ArtNet.OpOf(p) != ArtOp.PollReply)
        {
            why = p.Length < 10 ? $"{p.Length} bytes is shorter than an Art-Net header" : "not an ArtPollReply";
            return false;
        }
        if (p.Length < ReplyMinimum)
        {
            why = $"an ArtPollReply of {p.Length} bytes is shorter than its {ReplyMinimum}-byte fixed part";
            return false;
        }
        var ports = Math.Min((p[172] << 8) | p[173], 4);
        var net = p[18] & 0x7F;
        var sub = p[19] & 0x0F;
        var universes = ImmutableArray.CreateBuilder<int>(ports);
        for (var i = 0; i < ports; i++)
        {
            // Bit 7 of PortTypes: the port can output DMX from the network (the node receives our universes).
            if ((p[174 + i] & 0x80) == 0)
                continue;
            universes.Add((net << 8) | (sub << 4) | (p[190 + i] & 0x0F));
        }
        var refresh = p.Length >= 228 ? (p[226] << 8) | p[227] : 0;
        var mac = string.Join(':', p.Slice(201, 6).ToArray().Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        reply = new ArtNodeReply(
            string.Join('.', p[10], p[11], p[12], p[13]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[14..]),
            Text(p.Slice(26, 18)),
            Text(p.Slice(44, 64)),
            universes.ToImmutable(),
            mac,
            refresh <= 44 ? 44 : refresh,
            (p[20] << 8) | p[21],
            p.Length >= 26 ? (p[25] << 8) | p[24] : 0);
        why = "";
        return true;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var s = Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
        return new string(s.Where(c => c >= ' ' && c < 0x7F).ToArray()).Trim();
    }
}

/// <summary>DDP (Distributed Display Protocol, UDP 4048) as LED output writes it: a 10-byte header — flags (version 1 in bits 6–7,
/// push in bit 0), a 4-bit sequence (1–15), the data type, the destination id (1 = the display), the byte offset and the data
/// length, both big-endian — then at most 1440 bytes of data.</summary>
public static class Ddp
{
    public const int Port = 4048;

    public const int HeaderLength = 10;

    /// <summary>The most data one packet carries (a multiple of 3 and of 4, so no RGB or RGBW pixel straddles two packets).</summary>
    public const int MaxData = 1440;

    public const byte Version1 = 0x40;

    public const byte PushFlag = 0x01;

    /// <summary>RGB, 8 bits a channel.</summary>
    public const byte Rgb8 = 0x0B;

    /// <summary>RGBW, 8 bits a channel.</summary>
    public const byte Rgbw8 = 0x1B;

    /// <summary>RGB, 16 bits a channel.</summary>
    public const byte Rgb16 = 0x0C;

    /// <summary>RGBW, 16 bits a channel.</summary>
    public const byte Rgbw16 = 0x1C;

    public const byte Display = 1;

    /// <summary>Writes one packet; answers its length.</summary>
    public static int Write(Span<byte> into, bool push, byte sequence, byte dataType, int offset, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxData)
            throw new ArgumentException($"a DDP packet carries at most {MaxData} bytes", nameof(data));
        into[0] = (byte)(Version1 | (push ? PushFlag : 0));
        into[1] = (byte)(sequence & 0x0F);
        into[2] = dataType;
        into[3] = Display;
        BinaryPrimitives.WriteUInt32BigEndian(into[4..], (uint)offset);
        BinaryPrimitives.WriteUInt16BigEndian(into[8..], (ushort)data.Length);
        data.CopyTo(into[HeaderLength..]);
        return HeaderLength + data.Length;
    }

    /// <summary>A packet's header: false with the reason for one that is short, of another version, or longer than it says.</summary>
    public static bool TryRead(ReadOnlySpan<byte> p, out DdpHeader header, out ReadOnlySpan<byte> data, out string why)
    {
        header = default;
        data = default;
        if (p.Length < HeaderLength)
        {
            why = $"{p.Length} bytes is shorter than DDP's 10-byte header";
            return false;
        }
        if ((p[0] & 0xC0) != Version1)
        {
            why = $"DDP version {(p[0] >> 6).ToString(System.Globalization.CultureInfo.InvariantCulture)} is not version 1";
            return false;
        }
        var length = BinaryPrimitives.ReadUInt16BigEndian(p[8..]);
        if (length > MaxData || HeaderLength + length > p.Length)
        {
            why = $"a data length of {length} does not fit the packet's {p.Length - HeaderLength} bytes";
            return false;
        }
        header = new DdpHeader((p[0] & PushFlag) != 0, (byte)(p[1] & 0x0F), p[2], p[3], (int)BinaryPrimitives.ReadUInt32BigEndian(p[4..]), length);
        data = p.Slice(HeaderLength, length);
        why = "";
        return true;
    }
}

/// <summary>A DDP packet's header fields.</summary>
public readonly record struct DdpHeader(bool Push, byte Sequence, byte DataType, byte Destination, int Offset, int Length);
