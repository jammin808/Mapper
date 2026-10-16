using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>One station for <see cref="E57TestWriter"/>: points in the scan's own frame, optional colours and grid cells, which
/// points are invalid, how the coordinates are stored (ScaledInteger with a scale, or Float single or double), and the pose.</summary>
internal sealed record E57TestScan(string Name, Vec3d[] Points)
{
    public int[]? Colours { get; init; }
    public (int Row, int Column)[]? Cells { get; init; }
    public bool[]? Invalid { get; init; }
    /// <summary>"scaled", "double" or "single".</summary>
    public string Storage { get; init; } = "scaled";
    public double Scale { get; init; } = 0.001;
    public (double W, double X, double Y, double Z) Rotation { get; init; } = (1, 0, 0, 0);
    public Vec3d Translation { get; init; } = Vec3d.Zero;
    /// <summary>Records per data packet (small, so fields straddle packets).</summary>
    public int PerPacket { get; init; } = 97;
}

/// <summary>Writes minimal valid E57 files for the reader's tests, from the format as libE57Format writes it: the 48-byte header, the
/// logical stream cut into 1020-byte pages each followed by its CRC-32C (stored big-endian), a compressed-vector binary section per
/// scan (section header, data packets with one bit-packed byte stream per prototype field), then the XML.</summary>
internal static class E57TestWriter
{
    private const int Page = 1020;

    private static long Physical(long logical) => logical / Page * 1024 + logical % Page;

    private sealed record Field(string Name, string Xml, int Bits, Func<int, ulong> Raw);

    public static byte[] Write(IReadOnlyList<E57TestScan> scans, string coordinateMetadata = "")
    {
        var logical = new List<byte>(new byte[48]);
        var xmlScans = new StringBuilder();
        foreach (var scan in scans)
        {
            while (logical.Count % 4 != 0)
                logical.Add(0);
            var fields = Fields(scan);
            var sectionAt = logical.Count;
            logical.AddRange(new byte[32]);
            var dataAt = logical.Count;
            // Every field's whole bit stream, then cut into per-packet runs of PerPacket records' worth of bytes.
            var streams = fields.Select(f => Pack(f, scan.Points.Length)).ToList();
            var cursors = new int[fields.Count];
            var packets = (scan.Points.Length + scan.PerPacket - 1) / scan.PerPacket;
            for (var k = 0; k < packets; k++)
            {
                var records = Math.Min(scan.PerPacket, scan.Points.Length - k * scan.PerPacket);
                var buffers = new List<byte[]>();
                for (var f = 0; f < fields.Count; f++)
                {
                    var endBits = (long)(k * scan.PerPacket + records) * fields[f].Bits;
                    var endByte = k == packets - 1 ? streams[f].Length : (int)(endBits / 8);
                    buffers.Add(streams[f][cursors[f]..endByte]);
                    cursors[f] = endByte;
                }
                var length = 6 + 2 * fields.Count + buffers.Sum(b => b.Length);
                var padded = (length + 3) / 4 * 4;
                var packet = new byte[padded];
                packet[0] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(padded - 1));
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)fields.Count);
                var at = 6 + 2 * fields.Count;
                for (var f = 0; f < fields.Count; f++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6 + 2 * f), (ushort)buffers[f].Length);
                    buffers[f].CopyTo(packet, at);
                    at += buffers[f].Length;
                }
                logical.AddRange(packet);
            }
            var header = new byte[32];
            header[0] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)(logical.Count - sectionAt));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)Physical(dataAt));
            for (var i = 0; i < 32; i++)
                logical[sectionAt + i] = header[i];
            xmlScans.Append(ScanXml(scan, fields, Physical(sectionAt)));
        }
        var xml = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<e57Root type=\"Structure\" xmlns=\"http://www.astm.org/COMMIT/E57/2010-e57-v1.0\">\n"
            + "<formatName type=\"String\"><![CDATA[ASTM E57 3D Imaging Data File]]></formatName>\n"
            + "<guid type=\"String\"><![CDATA[{6107F6B1-0000-0000-0000-000000000001}]]></guid>\n"
            + "<versionMajor type=\"Integer\">1</versionMajor>\n<versionMinor type=\"Integer\">0</versionMinor>\n"
            + (coordinateMetadata.Length > 0 ? $"<coordinateMetadata type=\"String\"><![CDATA[{coordinateMetadata}]]></coordinateMetadata>\n" : "")
            + "<data3D type=\"Vector\" allowHeterogeneousChildren=\"1\">\n" + xmlScans + "</data3D>\n"
            + "<images2D type=\"Vector\" allowHeterogeneousChildren=\"1\"/>\n</e57Root>\n");
        var xmlAt = logical.Count;
        logical.AddRange(xml);
        var pages = (logical.Count + Page - 1) / Page;
        var head = new byte[48];
        Encoding.ASCII.GetBytes("ASTM-E57").CopyTo(head, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(16), (ulong)pages * 1024);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(24), (ulong)Physical(xmlAt));
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(32), (ulong)xml.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(40), 1024);
        for (var i = 0; i < 48; i++)
            logical[i] = head[i];
        var file = new byte[pages * 1024];
        for (var p = 0; p < pages; p++)
        {
            var take = Math.Min(Page, logical.Count - p * Page);
            for (var i = 0; i < take; i++)
                file[p * 1024 + i] = logical[p * Page + i];
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(p * 1024 + Page), MeshIO.Crc32C(file.AsSpan(p * 1024, Page)));
        }
        return file;
    }

    private static string X(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static List<Field> Fields(E57TestScan scan)
    {
        var fields = new List<Field>();
        var pts = scan.Points;
        string[] axes = ["cartesianX", "cartesianY", "cartesianZ"];
        for (var a = 0; a < 3; a++)
        {
            var axis = a;
            double C(int i) => axis == 0 ? pts[i].X : axis == 1 ? pts[i].Y : pts[i].Z;
            switch (scan.Storage)
            {
                case "scaled":
                {
                    var raws = Enumerable.Range(0, pts.Length).Select(i => (long)Math.Round(C(i) / scan.Scale)).ToArray();
                    var min = raws.Length > 0 ? raws.Min() - 3 : 0;
                    var max = raws.Length > 0 ? raws.Max() + 5 : 0;
                    var bits = max > min ? 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(max - min)) : 0;
                    fields.Add(new Field(axes[a], $"<{axes[a]} type=\"ScaledInteger\" minimum=\"{min}\" maximum=\"{max}\" scale=\"{X(scan.Scale)}\" offset=\"0\"/>",
                        bits, i => (ulong)(raws[i] - min)));
                    break;
                }
                case "single":
                    fields.Add(new Field(axes[a], $"<{axes[a]} type=\"Float\" precision=\"single\"/>", 32, i => (uint)BitConverter.SingleToInt32Bits((float)C(i))));
                    break;
                default:
                    fields.Add(new Field(axes[a], $"<{axes[a]} type=\"Float\"/>", 64, i => (ulong)BitConverter.DoubleToInt64Bits(C(i))));
                    break;
            }
        }
        if (scan.Colours is { } colours)
        {
            fields.Add(new Field("colorRed", "<colorRed type=\"Integer\" minimum=\"0\" maximum=\"255\"/>", 8, i => (ulong)((colours[i] >> 16) & 255)));
            fields.Add(new Field("colorGreen", "<colorGreen type=\"Integer\" minimum=\"0\" maximum=\"255\"/>", 8, i => (ulong)((colours[i] >> 8) & 255)));
            fields.Add(new Field("colorBlue", "<colorBlue type=\"Integer\" minimum=\"0\" maximum=\"255\"/>", 8, i => (ulong)(colours[i] & 255)));
        }
        if (scan.Cells is { } cells)
        {
            var rows = cells.Max(c => c.Row);
            var cols = cells.Max(c => c.Column);
            int Bits(int max) => max == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max);
            fields.Add(new Field("rowIndex", $"<rowIndex type=\"Integer\" minimum=\"0\" maximum=\"{rows}\"/>", Bits(rows), i => (ulong)cells[i].Row));
            fields.Add(new Field("columnIndex", $"<columnIndex type=\"Integer\" minimum=\"0\" maximum=\"{cols}\"/>", Bits(cols), i => (ulong)cells[i].Column));
        }
        if (scan.Invalid is { } invalid)
            fields.Add(new Field("cartesianInvalidState", "<cartesianInvalidState type=\"Integer\" minimum=\"0\" maximum=\"2\"/>", 2, i => invalid[i] ? 2UL : 0UL));
        return fields;
    }

    /// <summary>A field's values bit-packed least significant bit first, whole stream.</summary>
    private static byte[] Pack(Field f, int count)
    {
        var bytes = new byte[((long)count * f.Bits + 7) / 8];
        long bit = 0;
        for (var i = 0; i < count; i++)
        {
            var v = f.Raw(i);
            for (var b = 0; b < f.Bits; b++, bit++)
                if (((v >> b) & 1) != 0)
                    bytes[bit >> 3] |= (byte)(1 << (int)(bit & 7));
        }
        return bytes;
    }

    private static string ScanXml(E57TestScan scan, List<Field> fields, long sectionPhysical)
    {
        var (w, x, y, z) = scan.Rotation;
        var t = scan.Translation;
        return "<vectorChild type=\"Structure\">\n"
            + $"<guid type=\"String\"><![CDATA[{{{scan.Name.GetHashCode(StringComparison.Ordinal):X8}-0000-0000-0000-000000000000}}]]></guid>\n"
            + $"<name type=\"String\"><![CDATA[{SecurityElement.Escape(scan.Name)}]]></name>\n"
            + "<pose type=\"Structure\"><rotation type=\"Structure\">"
            + $"<w type=\"Float\">{X(w)}</w><x type=\"Float\">{X(x)}</x><y type=\"Float\">{X(y)}</y><z type=\"Float\">{X(z)}</z></rotation>"
            + $"<translation type=\"Structure\"><x type=\"Float\">{X(t.X)}</x><y type=\"Float\">{X(t.Y)}</y><z type=\"Float\">{X(t.Z)}</z></translation></pose>\n"
            + $"<points type=\"CompressedVector\" fileOffset=\"{sectionPhysical}\" recordCount=\"{scan.Points.Length}\">\n"
            + "<prototype type=\"Structure\">" + string.Concat(fields.Select(f => f.Xml)) + "</prototype>\n"
            + "<codecs type=\"Vector\" allowHeterogeneousChildren=\"1\"/>\n</points>\n</vectorChild>\n";
    }
}
