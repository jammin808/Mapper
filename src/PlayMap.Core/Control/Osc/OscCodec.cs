using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PlayMap.Core.Control.Osc;

/// <summary>One OSC argument by its type tag: i (int32), h (int64), c (char), r (RGBA), m (MIDI) carry <see cref="Int"/>; f
/// (float32), d (float64) carry <see cref="Float"/>; s and S carry <see cref="Text"/>; b carries <see cref="Blob"/>; t a timetag in
/// <see cref="Int"/>; T, F, N, I carry nothing; '[' an array of <see cref="Items"/>.</summary>
public sealed record OscArg(char Tag, long Int, double Float, string? Text, ImmutableArray<byte> Blob, ImmutableArray<OscArg> Items)
{
    public static OscArg I(int v) => new('i', v, v, null, [], []);
    public static OscArg H(long v) => new('h', v, v, null, [], []);
    public static OscArg F(float v) => new('f', 0, v, null, [], []);
    public static OscArg D(double v) => new('d', 0, v, null, [], []);
    public static OscArg S(string v) => new('s', 0, 0, v, [], []);
    public static OscArg B(ReadOnlySpan<byte> v) => new('b', 0, 0, null, [.. v], []);
    public static OscArg Bool(bool v) => new(v ? 'T' : 'F', v ? 1 : 0, v ? 1 : 0, null, [], []);
    public static OscArg Nil() => new('N', 0, 0, null, [], []);
    public static OscArg Array(params OscArg[] items) => new('[', 0, 0, null, [], [.. items]);

    public bool IsNumber => Tag is 'i' or 'h' or 'f' or 'd' or 'T' or 'F';

    /// <summary>The argument as a number (T 1, F 0), or NaN.</summary>
    public double Number => Tag switch
    {
        'i' or 'h' or 'c' => Int,
        'f' or 'd' => Float,
        'T' => 1,
        'F' => 0,
        _ => double.NaN,
    };

    public bool Equals(OscArg? other) => other is not null && Tag == other.Tag && Int == other.Int
        && (Float.Equals(other.Float)) && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && Blob.AsSpan().SequenceEqual(other.Blob.AsSpan()) && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Tag, Int, Float, Text, Blob.Length, Items.Length);

    public override string ToString() => Tag switch
    {
        'i' or 'h' or 'c' or 'r' or 'm' or 't' => Tag + ":" + Int.ToString(CultureInfo.InvariantCulture),
        'f' or 'd' => Tag + ":" + Float.ToString("R", CultureInfo.InvariantCulture),
        's' or 'S' => Tag + ":" + Text,
        'b' => "b:" + Convert.ToHexString(Blob.AsSpan()),
        '[' => "[" + string.Join(",", Items) + "]",
        _ => Tag.ToString(),
    };
}

/// <summary>An OSC packet: a message or a bundle.</summary>
public interface IOscPacket
{
    bool IsBundle { get; }
}

/// <summary>An OSC message: an address and its arguments.</summary>
public sealed record OscMessage(string Address, ImmutableArray<OscArg> Args) : IOscPacket
{
    public bool IsBundle => false;

    public static OscMessage Of(string address, params OscArg[] args) => new(address, [.. args]);

    public bool Equals(OscMessage? other) => other is not null && string.Equals(Address, other.Address, StringComparison.Ordinal) && Args.SequenceEqual(other.Args);

    public override int GetHashCode() => HashCode.Combine(Address, Args.Length);

    public override string ToString() => Address + " " + string.Join(" ", Args);
}

/// <summary>An OSC bundle: a timetag (NTP 32.32; 1 = immediately) and its elements, run together.</summary>
public sealed record OscBundle(ulong TimeTag, ImmutableArray<IOscPacket> Elements) : IOscPacket
{
    public bool IsBundle => true;

    public const ulong Immediately = 1;

    public bool Equals(OscBundle? other) => other is not null && TimeTag == other.TimeTag && Elements.SequenceEqual(other.Elements);

    public override int GetHashCode() => HashCode.Combine(TimeTag, Elements.Length);

    /// <summary>A timetag as Unix milliseconds (NTP's epoch is 1900; 2 208 988 800 s before Unix's).</summary>
    public static long UnixMs(ulong timeTag)
    {
        var seconds = (long)(timeTag >> 32) - 2_208_988_800L;
        var fraction = (long)((timeTag & 0xFFFF_FFFFUL) * 1000 >> 32);
        return seconds * 1000 + fraction;
    }

    public static ulong TimeTagOf(long unixMs)
    {
        var seconds = (ulong)(unixMs / 1000 + 2_208_988_800L);
        var fraction = ((ulong)(unixMs % 1000) << 32) / 1000;
        return (seconds << 32) | fraction;
    }
}

/// <summary>OSC 1.0 (opensoundcontrol.org, "The Open Sound Control 1.0 Specification"): big-endian, 4-byte aligned strings and
/// blobs, the type tags i f s b h t d S c r m T F N I and arrays, bundles nested to depth 8. A message without a type tag string
/// reads as no arguments (the specification's robustness note); a message with an unknown tag is dropped (in a bundle, only that
/// element); anything else malformed is refused with the reason, never thrown.</summary>
public static class OscCodec
{
    public const int MaxDepth = 8;

    private static readonly byte[] BundleHead = "#bundle\0"u8.ToArray();

    /// <summary>Reads a packet; false with the reason when it is malformed (an unknown tag in a message, a short string…).</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out IOscPacket? packet, out string why) => TryRead(data, 0, out packet, out why);

    private static bool TryRead(ReadOnlySpan<byte> data, int depth, out IOscPacket? packet, out string why)
    {
        packet = null;
        why = "";
        if (data.Length == 0 || data.Length % 4 != 0)
        {
            why = "an OSC packet is a non-zero multiple of 4 bytes";
            return false;
        }
        if (data[0] == '#')
            return TryBundle(data, depth, out packet, out why);
        if (data[0] != '/')
        {
            why = "an OSC address starts with '/'";
            return false;
        }
        if (!TryMessage(data, out var message, out why))
            return false;
        packet = message;
        return true;
    }

    private static bool TryBundle(ReadOnlySpan<byte> data, int depth, out IOscPacket? packet, out string why)
    {
        packet = null;
        if (depth >= MaxDepth)
        {
            why = "OSC bundles nest at most 8 deep";
            return false;
        }
        if (data.Length < 16 || !data[..8].SequenceEqual(BundleHead))
        {
            why = "an OSC bundle starts with \"#bundle\" and a timetag";
            return false;
        }
        var tag = BinaryPrimitives.ReadUInt64BigEndian(data[8..16]);
        var elements = ImmutableArray.CreateBuilder<IOscPacket>();
        var at = 16;
        while (at < data.Length)
        {
            if (at + 4 > data.Length)
            {
                why = "an OSC bundle element is cut short";
                return false;
            }
            var size = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            at += 4;
            if (size <= 0 || size % 4 != 0 || at + size > data.Length)
            {
                why = "an OSC bundle element's size is wrong";
                return false;
            }
            var element = data.Slice(at, size);
            at += size;
            if (TryRead(element, depth + 1, out var inner, out var innerWhy))
                elements.Add(inner!);
            else if (!innerWhy.StartsWith("unknown type tag", StringComparison.Ordinal))
            {
                why = innerWhy;
                return false;
            }
        }
        why = "";
        packet = new OscBundle(tag, elements.ToImmutable());
        return true;
    }

    private static bool TryMessage(ReadOnlySpan<byte> data, out OscMessage? message, out string why)
    {
        message = null;
        var at = 0;
        if (!TryString(data, ref at, out var address, out why))
            return false;
        if (at >= data.Length || data[at] != ',')
        {
            message = new OscMessage(address, ImmutableArray<OscArg>.Empty);
            return true;
        }
        if (!TryString(data, ref at, out var tags, out why))
            return false;
        var stack = new Stack<ImmutableArray<OscArg>.Builder>();
        var args = ImmutableArray.CreateBuilder<OscArg>();
        for (var i = 1; i < tags.Length; i++)
        {
            var t = tags[i];
            OscArg arg;
            switch (t)
            {
                case 'i' or 'c' or 'r' or 'm':
                    if (!Need(data, at, 4, out why))
                        return false;
                    arg = t == 'i' || t == 'c'
                        ? new OscArg(t, BinaryPrimitives.ReadInt32BigEndian(data[at..]), BinaryPrimitives.ReadInt32BigEndian(data[at..]), null, [], [])
                        : new OscArg(t, BinaryPrimitives.ReadUInt32BigEndian(data[at..]), 0, null, [], []);
                    at += 4;
                    break;
                case 'f':
                    if (!Need(data, at, 4, out why))
                        return false;
                    arg = new OscArg('f', 0, BinaryPrimitives.ReadSingleBigEndian(data[at..]), null, [], []);
                    at += 4;
                    break;
                case 'h' or 't':
                    if (!Need(data, at, 8, out why))
                        return false;
                    var h = BinaryPrimitives.ReadInt64BigEndian(data[at..]);
                    arg = new OscArg(t, h, t == 'h' ? h : 0, null, [], []);
                    at += 8;
                    break;
                case 'd':
                    if (!Need(data, at, 8, out why))
                        return false;
                    arg = new OscArg('d', 0, BinaryPrimitives.ReadDoubleBigEndian(data[at..]), null, [], []);
                    at += 8;
                    break;
                case 's' or 'S':
                    if (!TryString(data, ref at, out var s, out why))
                        return false;
                    arg = new OscArg(t, 0, 0, s, [], []);
                    break;
                case 'b':
                    if (!Need(data, at, 4, out why))
                        return false;
                    var n = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
                    at += 4;
                    if (n < 0 || !Need(data, at, n, out why))
                    {
                        why = "an OSC blob is cut short";
                        return false;
                    }
                    arg = new OscArg('b', 0, 0, null, [.. data.Slice(at, n)], []);
                    at += Pad(n);
                    break;
                case 'T' or 'F':
                    arg = OscArg.Bool(t == 'T');
                    break;
                case 'N' or 'I':
                    arg = new OscArg(t, 0, 0, null, [], []);
                    break;
                case '[':
                    stack.Push(args);
                    args = ImmutableArray.CreateBuilder<OscArg>();
                    continue;
                case ']':
                    if (stack.Count == 0)
                    {
                        why = "an OSC array closes without opening";
                        return false;
                    }
                    var items = args.ToImmutable();
                    args = stack.Pop();
                    args.Add(new OscArg('[', 0, 0, null, [], items));
                    continue;
                default:
                    why = $"unknown type tag '{t}' — the message is dropped";
                    return false;
            }
            args.Add(arg);
        }
        if (stack.Count > 0)
        {
            why = "an OSC array opens without closing";
            return false;
        }
        message = new OscMessage(address, args.ToImmutable());
        why = "";
        return true;
    }

    private static bool Need(ReadOnlySpan<byte> data, int at, int n, out string why)
    {
        why = at + n <= data.Length ? "" : "an OSC argument is cut short";
        return why.Length == 0;
    }

    private static int Pad(int n) => (n + 3) & ~3;

    private static bool TryString(ReadOnlySpan<byte> data, ref int at, out string s, out string why)
    {
        s = "";
        var end = data[at..].IndexOf((byte)0);
        if (end < 0)
        {
            why = "an OSC string has no terminating zero";
            return false;
        }
        s = Encoding.UTF8.GetString(data.Slice(at, end));
        at += Pad(end + 1);
        if (at > data.Length)
        {
            why = "an OSC string's padding is cut short";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>A packet as bytes.</summary>
    public static byte[] Write(IOscPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var buffer = new List<byte>(64);
        Write(packet, buffer);
        return [.. buffer];
    }

    private static void Write(IOscPacket packet, List<byte> o)
    {
        switch (packet)
        {
            case OscMessage m:
                WriteString(o, m.Address);
                var tags = new StringBuilder(",");
                foreach (var a in m.Args)
                    Tags(a, tags);
                WriteString(o, tags.ToString());
                foreach (var a in m.Args)
                    WriteArg(o, a);
                break;
            case OscBundle b:
                o.AddRange(BundleHead);
                var t = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(t, b.TimeTag);
                o.AddRange(t);
                var size = new byte[4];
                foreach (var e in b.Elements)
                {
                    var inner = Write(e);
                    BinaryPrimitives.WriteInt32BigEndian(size, inner.Length);
                    o.AddRange(size);
                    o.AddRange(inner);
                }
                break;
        }
    }

    private static void Tags(OscArg a, StringBuilder tags)
    {
        if (a.Tag != '[')
        {
            tags.Append(a.Tag);
            return;
        }
        tags.Append('[');
        foreach (var i in a.Items)
            Tags(i, tags);
        tags.Append(']');
    }

    private static void WriteArg(List<byte> o, OscArg a)
    {
        Span<byte> b = stackalloc byte[8];
        switch (a.Tag)
        {
            case 'i' or 'c':
                BinaryPrimitives.WriteInt32BigEndian(b, (int)a.Int);
                o.AddRange(b[..4].ToArray());
                break;
            case 'r' or 'm':
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)a.Int);
                o.AddRange(b[..4].ToArray());
                break;
            case 'f':
                BinaryPrimitives.WriteSingleBigEndian(b, (float)a.Float);
                o.AddRange(b[..4].ToArray());
                break;
            case 'h' or 't':
                BinaryPrimitives.WriteInt64BigEndian(b, a.Int);
                o.AddRange(b.ToArray());
                break;
            case 'd':
                BinaryPrimitives.WriteDoubleBigEndian(b, a.Float);
                o.AddRange(b.ToArray());
                break;
            case 's' or 'S':
                WriteString(o, a.Text ?? "");
                break;
            case 'b':
                BinaryPrimitives.WriteInt32BigEndian(b, a.Blob.Length);
                o.AddRange(b[..4].ToArray());
                o.AddRange(a.Blob);
                for (var i = a.Blob.Length; i < Pad(a.Blob.Length); i++)
                    o.Add(0);
                break;
            case '[':
                foreach (var i in a.Items)
                    WriteArg(o, i);
                break;
        }
    }

    private static void WriteString(List<byte> o, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        o.AddRange(bytes);
        var padded = Pad(bytes.Length + 1);
        for (var i = bytes.Length; i < padded; i++)
            o.Add(0);
    }
}
