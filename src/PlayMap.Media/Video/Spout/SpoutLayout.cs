using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using PlayMap.Core.Media;

namespace PlayMap.Media.Video.Spout;

/// <summary>A sender's shared texture as its 280-byte info map holds it (Spout 2.007 SharedTextureInfo): the share handle (32 bits,
/// as Spout truncates it), size, DXGI format, usage, a 256-byte description (the sender's exe path) and the partner id (0 for a
/// DirectX sender; 0x40000000 for a GL/DX-compatible one; 0x80000000 set when it shares through the CPU).</summary>
public readonly record struct SpoutInfo(uint ShareHandle, uint Width, uint Height, uint Format, uint Usage, string Description, uint PartnerId)
{
    public const int Size = 280;
    public const int DescriptionBytes = 256;

    public static SpoutInfo Read(ReadOnlySpan<byte> map)
    {
        if (map.Length < Size)
            throw new ArgumentException("a Spout info map is 280 bytes", nameof(map));
        var description = map.Slice(20, DescriptionBytes);
        var end = description.IndexOf((byte)0);
        return new SpoutInfo(BinaryPrimitives.ReadUInt32LittleEndian(map), BinaryPrimitives.ReadUInt32LittleEndian(map[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(map[8..]), BinaryPrimitives.ReadUInt32LittleEndian(map[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(map[16..]), Encoding.Latin1.GetString(end < 0 ? description : description[..end]),
            BinaryPrimitives.ReadUInt32LittleEndian(map[276..]));
    }

    /// <summary>Whether a map holds the same texture as <paramref name="info"/> (handle, size, format), without reading its text.</summary>
    public static bool SameTexture(ReadOnlySpan<byte> map, in SpoutInfo info) =>
        map.Length >= Size && BinaryPrimitives.ReadUInt32LittleEndian(map) == info.ShareHandle
        && BinaryPrimitives.ReadUInt32LittleEndian(map[4..]) == info.Width && BinaryPrimitives.ReadUInt32LittleEndian(map[8..]) == info.Height
        && BinaryPrimitives.ReadUInt32LittleEndian(map[12..]) == info.Format;

    public void Write(Span<byte> map)
    {
        if (map.Length < Size)
            throw new ArgumentException("a Spout info map is 280 bytes", nameof(map));
        map[..Size].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(map, ShareHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(map[4..], Width);
        BinaryPrimitives.WriteUInt32LittleEndian(map[8..], Height);
        BinaryPrimitives.WriteUInt32LittleEndian(map[12..], Format);
        BinaryPrimitives.WriteUInt32LittleEndian(map[16..], Usage);
        var d = Encoding.Latin1.GetBytes(Description);
        d.AsSpan(0, Math.Min(d.Length, DescriptionBytes - 1)).CopyTo(map[20..]);
        BinaryPrimitives.WriteUInt32LittleEndian(map[276..], PartnerId);
    }
}

/// <summary>Spout's names: the sender list map (<c>SpoutSenderNames</c>, 256-byte NUL-terminated slots, the set in order, ended by
/// an empty slot), the active sender map, and each sender's own objects. Pure.</summary>
public static class SpoutNames
{
    public const int NameBytes = 256;
    public const int MaxSenders = 64;
    public const string List = "SpoutSenderNames";
    public const string Active = "ActiveSenderName";

    /// <summary>The wait Spout takes on a shared map's mutex (67 ms), off the frame path only.</summary>
    public const int ListWaitMs = 67;

    public static string MutexOf(string map) => map + "_mutex";

    public static string AccessMutex(string sender) => sender + "_SpoutAccessMutex";

    public static string Counter(string sender) => sender + "_Count_Semaphore";

    public static string SyncEvent(string sender) => sender + "_Sync_Event";

    public static string DataMap(string sender) => sender + "_map";

    /// <summary>The names in a sender list map, in order, up to the first empty slot.</summary>
    public static List<string> Read(ReadOnlySpan<byte> map)
    {
        var names = new List<string>();
        for (var at = 0; at + NameBytes <= map.Length; at += NameBytes)
        {
            var slot = map.Slice(at, NameBytes);
            var end = slot.IndexOf((byte)0);
            if (end == 0)
                break;
            names.Add(Encoding.Latin1.GetString(end < 0 ? slot : slot[..end]));
        }
        return names;
    }

    /// <summary>Writes the names as Spout keeps them (a sorted set, ordinal) and an empty slot after them when there is room.</summary>
    public static void Write(Span<byte> map, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        map.Clear();
        var at = 0;
        foreach (var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (at + NameBytes > map.Length)
                break;
            var b = Encoding.Latin1.GetBytes(name);
            b.AsSpan(0, Math.Min(b.Length, NameBytes - 1)).CopyTo(map[at..]);
            at += NameBytes;
        }
    }

    /// <summary>Why a name cannot be a Spout sender's ("" when it can): printable ASCII (32–126) only, 1–255 bytes.</summary>
    public static string Invalid(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "a Spout name is 1–255 printable ASCII characters";
        if (name.Length > NameBytes - 1)
            return string.Create(CultureInfo.InvariantCulture, $"a Spout name is at most 255 characters ({name.Length} given)");
        foreach (var c in name)
            if (c is < ' ' or > '~')
                return $"a Spout name is printable ASCII only: '{name}' holds '{c}'";
        return "";
    }

    /// <summary>The default published name, "PlayMap &lt;node&gt; - &lt;send&gt;", with every character Spout cannot carry as '?'.</summary>
    public static string Default(string node, string send)
    {
        var text = $"PlayMap {node} - {send}";
        var chars = text.Select(c => c is < ' ' or > '~' ? '?' : c).ToArray();
        return new string(chars, 0, Math.Min(chars.Length, NameBytes - 1));
    }
}

/// <summary>The DXGI formats a Spout texture carries and how PlayMap reads them: B8G8R8A8 (87) and R8G8B8A8 (28) this round; a
/// format of 0 or a D3D9 one (21, 22) reads as B8G8R8A8, as Spout itself does.</summary>
public static class SpoutFormats
{
    public const uint Bgra8 = 87;
    public const uint Rgba8 = 28;

    /// <summary>The format a received info names, as read (0, 21 and 22 are B8G8R8A8).</summary>
    public static uint Normalise(uint format) => format is 0 or 21 or 22 ? Bgra8 : format;

    /// <summary>The CPU layout a received format lands in, or null with the words for a format received in a later round.</summary>
    public static PixelLayout? Layout(uint format, out string words)
    {
        words = "";
        switch (Normalise(format))
        {
            case Bgra8:
                return PixelLayout.Bgra8;
            case Rgba8:
                return PixelLayout.Rgba8;
            default:
                words = string.Create(CultureInfo.InvariantCulture,
                    $"the sender's texture is DXGI format {format}: PlayMap receives B8G8R8A8 and R8G8B8A8 Spout senders this round");
                return null;
        }
    }
}

/// <summary>A sender's data map (<c>&lt;sender&gt;_map</c>): the first 16 bytes hold the data's length as ASCII decimal, the data
/// starts at 16 and ends with a NUL. PlayMap writes its frame's facts there as JSON into a reused buffer (no allocation per frame).</summary>
public sealed class SpoutDataWriter
{
    public const int Header = 16;
    public const int DataBytes = 256;
    public const int MapBytes = DataBytes + 32;

    private readonly byte[] _json = new byte[DataBytes];
    private readonly byte[] _prefix;
    private readonly string _node;

    public SpoutDataWriter(string node, string send, string tap, string rate)
    {
        _node = node;
        _prefix = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $$"""{"pm":1,"node":"{{Clean(node)}}","send":"{{Clean(send)}}","rate":"{{Clean(rate)}}","tap":"{{Clean(tap)}}","frame":"""));
    }

    public string Node => _node;

    private static string Clean(string s) => new([.. s.Where(c => c is >= ' ' and <= '~' and not '"' and not '\\')]);

    /// <summary>Formats the frame's facts and writes them, with their length, into the map's bytes; false when they do not fit.</summary>
    public bool Write(Span<byte> map, long frame, long leaderNs, int width, int height)
    {
        var json = _json.AsSpan();
        var n = _prefix.Length;
        if (n >= json.Length)
            return false;
        _prefix.CopyTo(json);
        if (!Append(json, ref n, frame, ",\"leaderNs\":"u8) || !Append(json, ref n, leaderNs, ",\"w\":"u8)
            || !Append(json, ref n, width, ",\"h\":"u8) || !Append(json, ref n, height, "}"u8))
            return false;
        if (map.Length < Header + n + 1)
            return false;
        map[..Header].Clear();
        if (!Utf8Formatter.TryFormat(n, map, out _))
            return false;
        json[..n].CopyTo(map[Header..]);
        map[Header + n] = 0;
        return true;
    }

    private static bool Append(Span<byte> json, ref int n, long value, ReadOnlySpan<byte> after)
    {
        if (!Utf8Formatter.TryFormat(value, json[n..], out var written))
            return false;
        n += written;
        if (n + after.Length > json.Length)
            return false;
        after.CopyTo(json[n..]);
        n += after.Length;
        return true;
    }

    /// <summary>The data a map holds (tests and receivers that read PlayMap's facts).</summary>
    public static string Read(ReadOnlySpan<byte> map)
    {
        var header = map[..Header];
        var end = header.IndexOf((byte)0);
        if (!Utf8Parser.TryParse(end < 0 ? header : header[..end], out int length, out _) || length < 0 || Header + length > map.Length)
            return "";
        return Encoding.ASCII.GetString(map.Slice(Header, length));
    }
}
