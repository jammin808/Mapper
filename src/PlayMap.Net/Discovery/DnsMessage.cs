using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PlayMap.Net.Discovery;

/// <summary>The DNS record types mDNS and DNS-SD use here (RFC 1035, RFC 2782, RFC 3596, RFC 4034).</summary>
public enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Nsec = 47,
    Any = 255,
}

/// <summary>One question: a name, a type, and the mDNS "unicast response" bit (the class's top bit, RFC 6762 §5.4).</summary>
public sealed record DnsQuestion(string Name, DnsType Type, bool UnicastResponse = false);

/// <summary>One resource record. <see cref="Data"/> is the raw RDATA as it arrived (names in it already expanded for PTR and SRV);
/// <see cref="CacheFlush"/> is the class's top bit (RFC 6762 §10.2).</summary>
public sealed record DnsRecord(string Name, DnsType Type, uint Ttl, bool CacheFlush, ReadOnlyMemory<byte> Data)
{
    /// <summary>A PTR's target, an SRV's target; null for other types.</summary>
    public string? Target { get; init; }

    public ushort Port { get; init; }

    public ushort Priority { get; init; }

    public ushort Weight { get; init; }

    /// <summary>A TXT's strings.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];

    /// <summary>An A or AAAA record's address.</summary>
    public IPAddress? Address { get; init; }

    public static DnsRecord Ptr(string name, string target, uint ttl) =>
        new(name, DnsType.Ptr, ttl, false, DnsMessage.EncodeName(target)) { Target = target };

    public static DnsRecord Srv(string name, string target, ushort port, uint ttl, bool flush)
    {
        var t = DnsMessage.EncodeName(target);
        var data = new byte[6 + t.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port);
        t.CopyTo(data.AsSpan(6));
        return new DnsRecord(name, DnsType.Srv, ttl, flush, data) { Target = target, Port = port };
    }

    public static DnsRecord Txt(string name, IReadOnlyList<string> strings, uint ttl, bool flush)
    {
        using var ms = new MemoryStream();
        foreach (var s in strings.Count == 0 ? [""] : strings)
        {
            var b = Encoding.UTF8.GetBytes(s);
            if (b.Length > 255)
                throw new ArgumentException($"a TXT string is at most 255 bytes: '{s}'", nameof(strings));
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        return new DnsRecord(name, DnsType.Txt, ttl, flush, ms.ToArray()) { Text = strings };
    }

    public static DnsRecord A(string name, IPAddress address, uint ttl, bool flush)
    {
        ArgumentNullException.ThrowIfNull(address);
        var type = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? DnsType.Aaaa : DnsType.A;
        return new DnsRecord(name, type, ttl, flush, address.GetAddressBytes()) { Address = address };
    }

    /// <summary>The same record with another TTL (a goodbye is TTL 0; a legacy unicast answer at most 10 s).</summary>
    public DnsRecord WithTtl(uint ttl) => this with { Ttl = ttl };
}

/// <summary>A DNS message as mDNS uses it (RFC 1035 §4, RFC 6762): read with name compression, written without (allowed by RFC 6762
/// §18.14). A message that does not parse throws <see cref="InvalidDataException"/>.</summary>
public sealed record DnsMessage(ushort Id, bool IsResponse, bool Authoritative, IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsRecord> Answers, IReadOnlyList<DnsRecord> Authorities, IReadOnlyList<DnsRecord> Additionals)
{
    private const ushort ClassIn = 1;
    private const ushort TopBit = 0x8000;
    private const int MaxNameJumps = 32;

    public static DnsMessage Query(ushort id, params DnsQuestion[] questions) => new(id, false, false, questions, [], [], []);

    public byte[] Write()
    {
        using var ms = new MemoryStream(512);
        Span<byte> head = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(head, Id);
        ushort flags = 0;
        if (IsResponse)
            flags |= 0x8000;
        if (Authoritative)
            flags |= 0x0400;
        BinaryPrimitives.WriteUInt16BigEndian(head[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(head[4..], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(head[6..], (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(head[8..], (ushort)Authorities.Count);
        BinaryPrimitives.WriteUInt16BigEndian(head[10..], (ushort)Additionals.Count);
        ms.Write(head);
        Span<byte> two = stackalloc byte[2];
        Span<byte> four = stackalloc byte[4];
        foreach (var q in Questions)
        {
            ms.Write(EncodeName(q.Name));
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)q.Type);
            ms.Write(two);
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)(ClassIn | (q.UnicastResponse ? TopBit : 0)));
            ms.Write(two);
        }
        foreach (var r in Answers.Concat(Authorities).Concat(Additionals))
        {
            ms.Write(EncodeName(r.Name));
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)r.Type);
            ms.Write(two);
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)(ClassIn | (r.CacheFlush ? TopBit : 0)));
            ms.Write(two);
            BinaryPrimitives.WriteUInt32BigEndian(four, r.Ttl);
            ms.Write(four);
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)r.Data.Length);
            ms.Write(two);
            ms.Write(r.Data.Span);
        }
        return ms.ToArray();
    }

    public static DnsMessage Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12)
            throw new InvalidDataException("a DNS message is at least 12 bytes");
        var id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int qd = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]), ar = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        var at = 12;
        var questions = new List<DnsQuestion>(qd);
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(packet, ref at);
            Need(packet, at, 4);
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
            at += 4;
            questions.Add(new DnsQuestion(name, type, (cls & TopBit) != 0));
        }
        var answers = ReadRecords(packet, ref at, an);
        var authorities = ReadRecords(packet, ref at, ns);
        var additionals = ReadRecords(packet, ref at, ar);
        return new DnsMessage(id, (flags & 0x8000) != 0, (flags & 0x0400) != 0, questions, answers, authorities, additionals);
    }

    private static List<DnsRecord> ReadRecords(ReadOnlySpan<byte> packet, ref int at, int count)
    {
        var list = new List<DnsRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadName(packet, ref at);
            Need(packet, at, 10);
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(at + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 8)..]);
            at += 10;
            Need(packet, at, length);
            var start = at;
            var data = packet.Slice(at, length).ToArray();
            var record = new DnsRecord(name, type, ttl, (cls & TopBit) != 0, data);
            switch (type)
            {
                case DnsType.Ptr:
                {
                    var p = start;
                    record = record with { Target = ReadName(packet, ref p) };
                    break;
                }
                case DnsType.Srv when length >= 7:
                {
                    var p = start + 6;
                    record = record with
                    {
                        Priority = BinaryPrimitives.ReadUInt16BigEndian(packet[start..]),
                        Weight = BinaryPrimitives.ReadUInt16BigEndian(packet[(start + 2)..]),
                        Port = BinaryPrimitives.ReadUInt16BigEndian(packet[(start + 4)..]),
                        Target = ReadName(packet, ref p),
                    };
                    break;
                }
                case DnsType.Txt:
                {
                    var strings = new List<string>();
                    var p = 0;
                    while (p < data.Length)
                    {
                        int n = data[p++];
                        if (p + n > data.Length)
                            throw new InvalidDataException("a TXT string runs past its record");
                        strings.Add(Encoding.UTF8.GetString(data, p, n));
                        p += n;
                    }
                    record = record with { Text = strings };
                    break;
                }
                case DnsType.A when length == 4:
                case DnsType.Aaaa when length == 16:
                    record = record with { Address = new IPAddress(data) };
                    break;
            }
            at += length;
            list.Add(record);
        }
        return list;
    }

    private static void Need(ReadOnlySpan<byte> packet, int at, int n)
    {
        if (at < 0 || n < 0 || at + n > packet.Length)
            throw new InvalidDataException("a DNS message ends inside a record");
    }

    /// <summary>A name, following compression pointers (bounded, so a loop of pointers is refused).</summary>
    public static string ReadName(ReadOnlySpan<byte> packet, ref int at)
    {
        var labels = new List<string>();
        var p = at;
        var jumped = false;
        for (var jumps = 0; ;)
        {
            Need(packet, p, 1);
            int len = packet[p];
            if (len == 0)
            {
                p++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                Need(packet, p, 2);
                var target = ((len & 0x3F) << 8) | packet[p + 1];
                if (!jumped)
                    at = p + 2;
                jumped = true;
                if (++jumps > MaxNameJumps || target >= packet.Length)
                    throw new InvalidDataException("a DNS name's pointers loop or point outside the message");
                p = target;
                continue;
            }
            if ((len & 0xC0) != 0)
                throw new InvalidDataException("a DNS label type this reader does not know");
            Need(packet, p + 1, len);
            labels.Add(Encoding.UTF8.GetString(packet.Slice(p + 1, len)));
            p += 1 + len;
        }
        if (!jumped)
            at = p;
        return string.Join('.', labels);
    }

    /// <summary>A name as labels (UTF-8, each at most 63 bytes), ending with the root label.</summary>
    public static byte[] EncodeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var ms = new MemoryStream(name.Length + 2);
        foreach (var label in SplitLabels(name))
        {
            var b = Encoding.UTF8.GetBytes(label);
            if (b.Length is 0 or > 63)
                throw new ArgumentException($"a DNS label is 1–63 bytes: '{label}' in '{name}'", nameof(name));
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    /// <summary>Labels of a name; an instance name may hold dots of its own only when written as "\." (RFC 6763 §4.3).</summary>
    private static IEnumerable<string> SplitLabels(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '\\' && i + 1 < name.Length)
            {
                sb.Append(name[++i]);
                continue;
            }
            if (c == '.')
            {
                if (sb.Length > 0)
                    yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    /// <summary>Names compare without case (RFC 6762 §16).</summary>
    public static bool SameName(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
