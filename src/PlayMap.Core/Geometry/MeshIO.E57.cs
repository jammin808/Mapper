using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>One scan (station) of an E57 file: its points in the file's frame (its pose applied, in double), whether it is a
/// structured scan (row and column indices: <see cref="Points"/> is then organised on that grid, invalid cells NaN), and its pose.</summary>
public sealed record E57Scan(string Name, string Guid, PointCloud Points, bool Structured, long Records, Vec3d Translation,
    (double W, double X, double Y, double Z) Rotation);

/// <summary>What an E57 file holds: its version, the coordinate system words it carries, and its scans.</summary>
public sealed record E57File(int Major, int Minor, string CoordinateMetadata, IReadOnlyList<E57Scan> Scans);

public static partial class MeshIO
{
    /// <summary>The physical page of an E57 file: 1020 bytes of data and a CRC-32C of them.</summary>
    public const int E57PageSize = 1024;

    private const int E57PageData = E57PageSize - 4;

    /// <summary>Reads an ASTM E57 file (managed, from the published format as libE57Format implements it): the 48-byte header,
    /// every page's CRC-32C checked (stored byte-swapped, as libE57Format writes it — a damaged page is named in the words), the XML
    /// read with no DTD and no resolver, and each <c>/data3D</c> scan's compressed vector decoded with the bit-pack codec: Float
    /// (single, double) and ScaledInteger (raw·scale + offset) or Integer fields at their declared widths, cartesian or spherical
    /// coordinates, <c>cartesianInvalidState</c>, colour and row/column indices. Each scan is moved into the file frame by its pose.
    /// Images (<c>/images2D</c>) are not read; a codec other than bit-pack is refused with words.</summary>
    public static E57File ReadE57(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var file = new E57Pages(stream);
        Span<byte> head = stackalloc byte[48];
        file.ReadLogical(0, head);
        if (Encoding.ASCII.GetString(head[..8]) != "ASTM-E57")
            throw new FormatException("Not an E57 file (no ASTM-E57 signature).");
        var major = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        var minor = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        var xmlAt = BinaryPrimitives.ReadUInt64LittleEndian(head[24..]);
        var xmlLength = BinaryPrimitives.ReadUInt64LittleEndian(head[32..]);
        var pageSize = BinaryPrimitives.ReadUInt64LittleEndian(head[40..]);
        if (pageSize != E57PageSize)
            throw new FormatException($"E57 pages of {pageSize} bytes are not read (1024 is the standard's).");
        if (major != 1)
            throw new NotSupportedException($"E57 version {major}.{minor} is not read (1.x is).");
        if (xmlLength is 0 or > 64 * 1024 * 1024)
            throw new FormatException("The E57 XML section has no sensible length.");
        if (xmlAt >= (ulong)file.PhysicalLength || (ulong)E57Pages.Logical((long)xmlAt) + xmlLength > (ulong)file.LogicalLength)
            throw new FormatException("The E57 XML section lies beyond the end of the file: the file is cut short or damaged.");
        var xml = new byte[(int)xmlLength];
        file.ReadLogical(E57Pages.Logical((long)xmlAt), xml);
        XDocument doc;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using (var reader = XmlReader.Create(new MemoryStream(xml), settings))
            doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("The E57 XML has no root.");
        var crs = Child(root, "coordinateMetadata")?.Value.Trim() ?? "";
        var scans = new List<E57Scan>();
        if (Child(root, "data3D") is { } data3D)
        {
            foreach (var scan in data3D.Elements())
            {
                ct.ThrowIfCancellationRequested();
                scans.Add(ReadE57Scan(file, scan, scans.Count, ct));
            }
        }
        return new E57File(major, minor, crs, scans);
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    private static string TypeOf(XElement e) => (string?)e.Attribute("type") ?? "";

    private static double Number(XElement? e, double fallback = 0) =>
        e is not null && double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long Int64Attr(XElement e, string name, long fallback) =>
        e.Attribute(name) is { } a && long.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double Attr(XElement e, string name, double fallback) =>
        e.Attribute(name) is { } a && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>One prototype field of a compressed vector and how its values are packed.</summary>
    private sealed class E57Field
    {
        public required string Name { get; init; }
        public required int Bits { get; init; }
        public bool IsFloat { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
        public double Scale { get; init; } = 1;
        public double Offset { get; init; }
        public long IntMin { get; init; }
        public List<byte> Bytes { get; } = [];
        public long BitAt;
        public long Decoded;

        public long Available(long records) => Bits == 0 ? records : Decoded + (Bytes.Count * 8L - BitAt) / Bits;

        public double Next()
        {
            if (Bits == 0)
            {
                Decoded++;
                return IsFloat ? Min : IntMin * Scale + Offset;
            }
            var raw = Take(Bits);
            Decoded++;
            if (IsFloat)
                return Bits == 32 ? BitConverter.Int32BitsToSingle((int)raw) : BitConverter.Int64BitsToDouble((long)raw);
            return unchecked((long)((ulong)IntMin + raw)) * Scale + Offset;
        }

        /// <summary>The next <paramref name="n"/> bits, least significant first (the bit-pack codec's order).</summary>
        private ulong Take(int n)
        {
            ulong v = 0;
            var got = 0;
            while (got < n)
            {
                var byteAt = (int)(BitAt >> 3);
                var bit = (int)(BitAt & 7);
                var take = Math.Min(8 - bit, n - got);
                var b = (ulong)((Bytes[byteAt] >> bit) & ((1 << take) - 1));
                v |= b << got;
                got += take;
                BitAt += take;
            }
            return v;
        }

        /// <summary>Drops the bytes every decoded value has passed.</summary>
        public void Compact()
        {
            var whole = (int)(BitAt >> 3);
            if (whole < 4096)
                return;
            Bytes.RemoveRange(0, whole);
            BitAt -= whole * 8L;
        }
    }

    private static E57Field FieldOf(XElement e)
    {
        var name = e.Name.LocalName;
        switch (TypeOf(e))
        {
            case "Float":
            {
                var single = ((string?)e.Attribute("precision") ?? "double") == "single";
                return new E57Field { Name = name, Bits = single ? 32 : 64, IsFloat = true, Min = Attr(e, "minimum", 0) };
            }
            case "Integer" or "ScaledInteger":
            {
                var scaled = TypeOf(e) == "ScaledInteger";
                // The standard's defaults are the whole int64 range (libE57Format omits a limit at its default).
                var min = Int64Attr(e, "minimum", long.MinValue);
                var max = Int64Attr(e, "maximum", long.MaxValue);
                if (max < min)
                    throw new FormatException($"the E57 field {name} has its maximum below its minimum");
                var span = unchecked((ulong)max - (ulong)min);
                var bits = span == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(span);
                return new E57Field
                {
                    Name = name,
                    Bits = bits,
                    IntMin = min,
                    Min = min,
                    Max = max,
                    Scale = scaled ? Attr(e, "scale", 1) : 1,
                    Offset = scaled ? Attr(e, "offset", 0) : 0,
                };
            }
            default:
                throw new NotSupportedException($"the E57 field {name} is of type {TypeOf(e)}, which a point record does not carry here");
        }
    }

    private static E57Scan ReadE57Scan(E57Pages file, XElement scan, int index, CancellationToken ct)
    {
        var name = Child(scan, "name")?.Value.Trim() ?? "Scan " + (index + 1).ToString(CultureInfo.InvariantCulture);
        var guid = Child(scan, "guid")?.Value.Trim() ?? "";
        var rotation = (W: 1.0, X: 0.0, Y: 0.0, Z: 0.0);
        var translation = Vec3d.Zero;
        if (Child(scan, "pose") is { } pose)
        {
            if (Child(pose, "rotation") is { } r)
                rotation = (Number(Child(r, "w"), 1), Number(Child(r, "x")), Number(Child(r, "y")), Number(Child(r, "z")));
            if (Child(pose, "translation") is { } t)
                translation = new Vec3d(Number(Child(t, "x")), Number(Child(t, "y")), Number(Child(t, "z")));
        }
        var points = Child(scan, "points") ?? throw new FormatException($"{name} has no points");
        if (TypeOf(points) != "CompressedVector")
            throw new FormatException($"{name}'s points are not a compressed vector");
        var codecs = Child(points, "codecs");
        if (codecs is not null && codecs.Elements().Any())
            throw new NotSupportedException($"{name} uses a codec other than bit-pack, which is not read");
        var records = (long)Attr(points, "recordCount", 0);
        var at = (long)Attr(points, "fileOffset", 0);
        if (records < 0)
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name} declares {records} points"));
        if (at < 0 || at >= file.PhysicalLength)
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name}'s points lie beyond the end of the file (offset {at:N0})"));
        var prototype = Child(points, "prototype") ?? throw new FormatException($"{name}'s points have no prototype");
        var fields = prototype.Elements().Select(FieldOf).ToList();
        int Find(string n) => fields.FindIndex(f => f.Name == n);
        int cx = Find("cartesianX"), cy = Find("cartesianY"), cz = Find("cartesianZ");
        int sr = Find("sphericalRange"), sa = Find("sphericalAzimuth"), se = Find("sphericalElevation");
        var cartesian = cx >= 0 && cy >= 0 && cz >= 0;
        if (!cartesian && (sr < 0 || sa < 0 || se < 0))
            throw new NotSupportedException($"{name} has neither cartesian nor spherical coordinates");
        int invalid = Find(cartesian ? "cartesianInvalidState" : "sphericalInvalidState");
        int red = Find("colorRed"), green = Find("colorGreen"), blue = Find("colorBlue");
        int row = Find("rowIndex"), column = Find("columnIndex");
        var colour = red >= 0 && green >= 0 && blue >= 0;
        var structured = row >= 0 && column >= 0;

        // The rotation of a unit quaternion (w, x, y, z).
        var (qw, qx, qy, qz) = rotation;
        var norm = Math.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (norm > 0)
            (qw, qx, qy, qz) = (qw / norm, qx / norm, qy / norm, qz / norm);
        var rot = new Mat3d(
            1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw),
            2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw),
            2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy));

        int Byte(E57Field f, double v) => f.Max > f.Min ? (int)Math.Clamp(Math.Round((v - f.Min) * 255 / (f.Max - f.Min)), 0, 255) : (int)Math.Clamp(v, 0, 255);

        // The binary section: its header, then data packets, each carrying a run of every field's byte stream.
        Span<byte> section = stackalloc byte[32];
        file.ReadLogical(E57Pages.Logical(at), section);
        if (section[0] != 1)
            throw new FormatException($"{name}'s binary section is not a compressed vector section (id {section[0]})");
        var sectionLength = BinaryPrimitives.ReadUInt64LittleEndian(section[8..]);
        if ((double)E57Pages.Logical(at) + sectionLength > file.LogicalLength)
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name}'s section of {sectionLength:N0} bytes runs past the end of the file"));
        // Every record takes its fields' bits of the section's byte streams: the count is held to what the section can carry before
        // anything is allocated for it, and a record of no bits (every field constant) carries nothing a count could be checked by.
        var bitsPerRecord = fields.Sum(f => (long)f.Bits);
        if (records > 0 && bitsPerRecord == 0)
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name}'s fields are all constant: its {records:N0} points carry no bits"));
        if ((double)records * bitsPerRecord > (double)sectionLength * 8)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                $"{name} declares {records:N0} points, more than its section of {sectionLength:N0} bytes holds at {bitsPerRecord} bits a point"));
        }
        var pts = new List<Vec3d>((int)Math.Min(records, 1 << 20));
        var cols = colour ? new List<int>(pts.Capacity) : null;
        var rows = structured ? new List<(int R, int C)>(pts.Capacity) : null;
        var dataAt = (long)BinaryPrimitives.ReadUInt64LittleEndian(section[16..]);
        var end = E57Pages.Logical(at) + (long)sectionLength;
        var cursor = E57Pages.Logical(dataAt);
        long done = 0;
        var header = new byte[6];
        var packet = new byte[65536];
        Span<double> v = stackalloc double[fields.Count];
        while (done < records)
        {
            ct.ThrowIfCancellationRequested();
            if (cursor + 4 > end)
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name} ends after {done:N0} of {records:N0} points"));
            file.ReadLogical(cursor, header.AsSpan(0, 4));
            var type = header[0];
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) + 1;
            if (type != 1)
            {
                // Index (0) and empty (2) packets carry no points.
                cursor += length;
                continue;
            }
            file.ReadLogical(cursor, packet.AsSpan(0, length));
            var streams = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4));
            if (streams != fields.Count)
                throw new FormatException($"{name}'s data packet carries {streams} streams for {fields.Count} fields");
            var bufferAt = 6 + 2 * streams;
            for (var s = 0; s < streams; s++)
            {
                var n = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6 + 2 * s));
                if (bufferAt + n > length)
                    throw new FormatException($"{name}'s data packet is shorter than its buffers");
                fields[s].Bytes.AddRange(packet.AsSpan(bufferAt, n));
                bufferAt += n;
            }
            cursor += length;
            var ready = fields.Min(f => f.Available(records));
            ready = Math.Min(ready, records);
            for (; done < ready; done++)
            {
                for (var f = 0; f < fields.Count; f++)
                    v[f] = fields[f].Next();
                if (invalid >= 0 && v[invalid] != 0)
                    continue;
                Vec3d local;
                if (cartesian)
                {
                    local = new Vec3d(v[cx], v[cy], v[cz]);
                }
                else
                {
                    double range = v[sr], az = v[sa], el = v[se];
                    local = new Vec3d(range * Math.Cos(el) * Math.Cos(az), range * Math.Cos(el) * Math.Sin(az), range * Math.Sin(el));
                }
                pts.Add(rot * local + translation);
                cols?.Add((Byte(fields[red], v[red]) << 16) | (Byte(fields[green], v[green]) << 8) | Byte(fields[blue], v[blue]));
                rows?.Add(((int)v[row], (int)v[column]));
            }
            foreach (var f in fields)
                f.Compact();
        }

        PointCloud cloud;
        if (structured && rows!.Count > 0)
        {
            var height = rows.Max(r => Math.Max(0L, r.R)) + 1;
            var width = rows.Max(r => Math.Max(0L, r.C)) + 1;
            // The grid organises the points it has: one far larger than the scan's records (invalid cells included) is a damaged
            // index, refused before the grid is allocated.
            if (width * height > Math.Min(MaxE57GridCells, Math.Max(E57GridCellsPerRecord * records, 1 << 20)))
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"{name}'s grid of {width:N0} × {height:N0} cells is far larger than its {records:N0} points: its row and column indices are damaged"));
            }
            var grid = new Vec3d[width * height];
            Array.Fill(grid, new Vec3d(double.NaN, double.NaN, double.NaN));
            var gridColours = colour ? new int[grid.Length] : null;
            for (var i = 0; i < pts.Count; i++)
            {
                var (r, c) = rows[i];
                if (r < 0 || c < 0)
                    continue;
                grid[r * width + c] = pts[i];
                if (gridColours is not null)
                    gridColours[r * width + c] = cols![i];
            }
            cloud = new PointCloud(grid, null, gridColours, (int)width, (int)height);
        }
        else
        {
            cloud = new PointCloud([.. pts], null, cols?.ToArray());
        }
        return new E57Scan(name, guid, cloud, structured, records, translation, rotation);
    }

    /// <summary>The most cells a structured scan's grid may have (24 bytes each).</summary>
    private const long MaxE57GridCells = 200_000_000;

    /// <summary>How many grid cells a structured scan may have for each record it declares (cells no record names are invalid).</summary>
    private const long E57GridCellsPerRecord = 16;

    /// <summary>An E57 file's pages: logical bytes read through the 1020-byte data of each 1024-byte page, each page's CRC-32C checked
    /// once on first read.</summary>
    private sealed class E57Pages(Stream stream)
    {
        private readonly HashSet<long> _checked = [];
        private readonly byte[] _page = new byte[E57PageSize];
        private long _cached = -1;

        /// <summary>The file's length in bytes (as large as a long holds when the stream cannot say).</summary>
        public long PhysicalLength { get; } = stream.CanSeek ? stream.Length : long.MaxValue;

        /// <summary>The logical bytes the file holds (its pages' data, the CRCs taken out).</summary>
        public long LogicalLength => PhysicalLength == long.MaxValue ? long.MaxValue : Logical(PhysicalLength);

        /// <summary>The logical offset of a physical one (the CRCs taken out).</summary>
        public static long Logical(long physical) => physical / E57PageSize * E57PageData + physical % E57PageSize;

        public void ReadLogical(long logical, Span<byte> into)
        {
            while (into.Length > 0)
            {
                var page = logical / E57PageData;
                var within = (int)(logical % E57PageData);
                Load(page);
                var n = Math.Min(into.Length, E57PageData - within);
                _page.AsSpan(within, n).CopyTo(into);
                into = into[n..];
                logical += n;
            }
        }

        private void Load(long page)
        {
            if (page == _cached)
                return;
            stream.Seek(page * E57PageSize, SeekOrigin.Begin);
            var got = 0;
            while (got < E57PageSize)
            {
                var n = stream.Read(_page, got, E57PageSize - got);
                if (n == 0)
                    break;
                got += n;
            }
            if (got < E57PageSize)
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"the E57 file ends inside page {page}"));
            if (_checked.Add(page))
            {
                var stored = BinaryPrimitives.ReadUInt32BigEndian(_page.AsSpan(E57PageData));
                var computed = Crc32C(_page.AsSpan(0, E57PageData));
                if (stored != computed)
                {
                    _checked.Remove(page);
                    throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                        $"E57 page {page} fails its CRC-32C check (bytes {page * E57PageSize:N0}–{page * E57PageSize + E57PageSize - 1:N0}): the file is damaged there"));
                }
            }
            _cached = page;
        }
    }

    /// <summary>CRC-32C (Castagnoli), reflected, initial and final value all ones — the checksum of each E57 page.</summary>
    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = ~0u;
        var i = 0;
        for (; i + 8 <= data.Length; i += 8)
            crc = (uint)BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
        for (; i < data.Length; i++)
            crc = BitOperations.Crc32C(crc, data[i]);
        return ~crc;
    }
}
