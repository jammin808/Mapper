using System.Buffers.Binary;
using System.Text;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

public static partial class MeshIO
{
    /// <summary>Reads XYZ / PTS text points: one point a line, separated by spaces, tabs, commas or semicolons.
    /// Columns: x y z, then by count — 4: intensity; 6: r g b (integers) or nx ny nz; 7: intensity r g b (PTS);
    /// 9: r g b nx ny nz or nx ny nz r g b. A PTS count line, header lines and comments are skipped. Intensity
    /// is not kept.</summary>
    public static PointCloud ReadXyz(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var pts = new List<Vec3d>();
        var nrm = new List<Vec3d>();
        var col = new List<int>();
        Span<Range> tok = stackalloc Range[16];
        Span<double> v = stackalloc double[16];
        ReadOnlySpan<char> separators = [' ', '\t', ',', ';'];
        int? columns = null;
        var colourAt = -1;
        var normalAt = -1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#' || span.StartsWith("//", StringComparison.Ordinal))
                continue;
            var n = Split(span, tok, separators);
            // A lone number is a PTS count line; fewer than three is not a point.
            if (n < 3)
                continue;
            var numeric = true;
            for (var k = 0; k < n && numeric; k++)
                numeric = TryNumber(span[tok[k]], out v[k]);
            if (!numeric)
                continue;
            if (columns is null)
            {
                columns = n;
                (colourAt, normalAt) = XyzLayout(n, v);
            }
            if (n < columns)
                continue;
            pts.Add(new Vec3d(v[0], v[1], v[2]));
            if (colourAt >= 0)
                col.Add(Rgb(v[colourAt], v[colourAt + 1], v[colourAt + 2]));
            if (normalAt >= 0)
                nrm.Add(new Vec3d(v[normalAt], v[normalAt + 1], v[normalAt + 2]));
        }
        return new PointCloud([.. pts], normalAt >= 0 ? [.. nrm] : null, colourAt >= 0 ? [.. col] : null);
    }

    /// <summary>Where colour and normal columns sit, judged from the first point's column count and whether
    /// the candidate colour values are whole numbers in 0..255.</summary>
    private static (int ColourAt, int NormalAt) XyzLayout(int n, ReadOnlySpan<double> v)
    {
        bool Bytes(int at, ReadOnlySpan<double> s) =>
            at + 2 < n && s[at..(at + 3)].ToArray().All(x => x >= 0 && x <= 255 && x == Math.Floor(x));
        return n switch
        {
            >= 9 when Bytes(3, v) => (3, 6),
            >= 9 => (Bytes(6, v) ? 6 : -1, 3),
            7 or 8 => (Bytes(4, v) ? 4 : -1, -1),
            6 => Bytes(3, v) ? (3, -1) : (-1, 3),
            _ => (-1, -1),
        };
    }

    /// <summary>Reads LAS 1.0–1.4 points (formats 0–10; compressed LAZ is refused): coordinates through the
    /// header's scale and offset, RGB when the format has it (16-bit values brought to 8 bits; files that
    /// store 8-bit values in the 16-bit fields are recognised). Coordinates are as stored (usually Z-up).</summary>
    public static PointCloud ReadLas(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[375];
        ReadExactly(stream, head.AsSpan(0, 227));
        if (Encoding.ASCII.GetString(head, 0, 4) != "LASF")
            throw new FormatException("Not a LAS file.");
        var minor = head[25];
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(94));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(96));
        if (headerSize < 227)
            throw new FormatException(string.Create(Inv, $"The LAS header says it is {headerSize} bytes, shorter than the 227 its own fields take: the file is damaged."));
        var formatByte = head[104];
        if ((formatByte & 0xC0) != 0)
            throw new NotSupportedException("Compressed LAS (LAZ) is not read; decompress it to LAS first.");
        var format = formatByte & 0x3F;
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(105));
        long count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(107));
        var scale = new Vec3d(ReadDouble(head, 131), ReadDouble(head, 139), ReadDouble(head, 147));
        var origin = new Vec3d(ReadDouble(head, 155), ReadDouble(head, 163), ReadDouble(head, 171));
        var read = 227;
        if (minor >= 4 && headerSize >= 375)
        {
            ReadExactly(stream, head.AsSpan(227, 375 - 227));
            read = 375;
            var count64 = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(247));
            if (count64 > long.MaxValue)
                throw new FormatException(string.Create(Inv, $"The LAS file declares {count64} points, more than any file holds: the header is damaged."));
            if (count == 0 || count64 > (ulong)count)
                count = (long)count64;
        }
        var (minLength, rgbAt) = format switch
        {
            0 => (20, -1),
            1 => (28, -1),
            2 => (26, 20),
            3 => (34, 28),
            4 => (57, -1),
            5 => (63, 28),
            6 => (30, -1),
            7 => (36, 30),
            8 => (38, 30),
            9 => (59, -1),
            10 => (67, 30),
            _ => throw new NotSupportedException($"LAS point format {format} is not known."),
        };
        if (recordLength < minLength)
            throw new FormatException($"LAS records of {recordLength} bytes are too short for format {format}.");
        if (offset < headerSize)
            throw new FormatException("The LAS point data starts inside the header.");
        // The whole cloud is held in memory: a count the file cannot hold is refused before it is allocated.
        if (stream.CanSeek && count > (stream.Length - offset) / recordLength)
        {
            throw new FormatException(string.Create(Inv,
                $"The LAS file declares {count:N0} points but holds at most {Math.Max(0, (stream.Length - offset) / recordLength):N0}: it is cut short or damaged."));
        }
        Skip(stream, offset - read);

        var pts = new Vec3d[count];
        var rgb = rgbAt >= 0 ? new ushort[count * 3] : null;
        var chunk = Math.Max(1, (1 << 20) / recordLength);
        var buffer = new byte[chunk * recordLength];
        long done = 0;
        var wide = false;
        while (done < count)
        {
            var take = (int)Math.Min(chunk, count - done);
            ReadExactly(stream, buffer.AsSpan(0, take * recordLength));
            for (var k = 0; k < take; k++)
            {
                var r = buffer.AsSpan(k * recordLength, recordLength);
                var i = done + k;
                pts[i] = new Vec3d(
                    BinaryPrimitives.ReadInt32LittleEndian(r) * scale.X + origin.X,
                    BinaryPrimitives.ReadInt32LittleEndian(r[4..]) * scale.Y + origin.Y,
                    BinaryPrimitives.ReadInt32LittleEndian(r[8..]) * scale.Z + origin.Z);
                if (rgb is null)
                    continue;
                for (var c = 0; c < 3; c++)
                {
                    var value = BinaryPrimitives.ReadUInt16LittleEndian(r[(rgbAt + 2 * c)..]);
                    rgb[i * 3 + c] = value;
                    wide |= value > 255;
                }
            }
            done += take;
        }
        int[]? colours = null;
        if (rgb is not null)
        {
            colours = new int[count];
            var shift = wide ? 8 : 0;
            for (long i = 0; i < count; i++)
                colours[i] = ((rgb[i * 3] >> shift) << 16) | ((rgb[i * 3 + 1] >> shift) << 8) | (rgb[i * 3 + 2] >> shift);
        }
        return new PointCloud(pts, null, colours);
    }

    private static double ReadDouble(byte[] b, int at) => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at));

    private static void ReadExactly(Stream stream, Span<byte> into)
    {
        try
        {
            stream.ReadExactly(into);
        }
        catch (EndOfStreamException e)
        {
            throw new FormatException("The file ends early.", e);
        }
    }

    private static void Skip(Stream stream, long bytes)
    {
        if (bytes <= 0)
            return;
        if (stream.CanSeek)
        {
            stream.Seek(bytes, SeekOrigin.Current);
            return;
        }
        var scratch = new byte[(int)Math.Min(bytes, 1 << 16)];
        while (bytes > 0)
        {
            var n = (int)Math.Min(bytes, scratch.Length);
            ReadExactly(stream, scratch.AsSpan(0, n));
            bytes -= n;
        }
    }
}
