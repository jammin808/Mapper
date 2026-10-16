using System.Buffers.Binary;
using System.Text;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>Writes LAS 1.4 files for the readers' tests, from the ASPRS LAS 1.4 specification: the 375-byte header, a projection VLR
/// (OGC WKT, record 2112, or a GeoKey directory, record 34735), and point format 7 (36 bytes: X Y Z, …, RGB at byte 30).</summary>
internal static class LasTestWriter
{
    public static byte[] Write(IReadOnlyList<Vec3d> points, IReadOnlyList<int>? colours, Vec3d scale, Vec3d offset, string? wkt = null, ushort? geoKeyEpsg = null)
    {
        var vlrs = new List<byte[]>();
        if (wkt is not null)
            vlrs.Add(Vlr("LASF_Projection", 2112, Encoding.ASCII.GetBytes(wkt + "\0")));
        if (geoKeyEpsg is { } code)
        {
            var keys = new byte[8 + 8];
            BinaryPrimitives.WriteUInt16LittleEndian(keys, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(8), 3072);
            BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(10), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(14), code);
            vlrs.Add(Vlr("LASF_Projection", 34735, keys));
        }
        const int Record = 36;
        var head = new byte[375];
        Encoding.ASCII.GetBytes("LASF").CopyTo(head, 0);
        head[24] = 1;
        head[25] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(94), 375);
        var pointsAt = 375 + vlrs.Sum(v => v.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(96), (uint)pointsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(100), (uint)vlrs.Count);
        head[104] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(105), Record);
        void D(int at, double v) => BinaryPrimitives.WriteDoubleLittleEndian(head.AsSpan(at), v);
        D(131, scale.X);
        D(139, scale.Y);
        D(147, scale.Z);
        D(155, offset.X);
        D(163, offset.Y);
        D(171, offset.Z);
        var min = points.Aggregate(new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue), Vec3d.Min);
        var max = points.Aggregate(new Vec3d(double.MinValue, double.MinValue, double.MinValue), Vec3d.Max);
        D(179, max.X);
        D(187, min.X);
        D(195, max.Y);
        D(203, min.Y);
        D(211, max.Z);
        D(219, min.Z);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(247), (ulong)points.Count);
        var body = new byte[points.Count * Record];
        for (var i = 0; i < points.Count; i++)
        {
            var r = body.AsSpan(i * Record, Record);
            BinaryPrimitives.WriteInt32LittleEndian(r, (int)Math.Round((points[i].X - offset.X) / scale.X));
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], (int)Math.Round((points[i].Y - offset.Y) / scale.Y));
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], (int)Math.Round((points[i].Z - offset.Z) / scale.Z));
            var c = colours?[i] ?? 0;
            BinaryPrimitives.WriteUInt16LittleEndian(r[30..], (ushort)(((c >> 16) & 255) * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(r[32..], (ushort)(((c >> 8) & 255) * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(r[34..], (ushort)((c & 255) * 257));
        }
        return [.. head, .. vlrs.SelectMany(v => v), .. body];
    }

    private static byte[] Vlr(string user, ushort record, byte[] data)
    {
        var h = new byte[54 + data.Length];
        Encoding.ASCII.GetBytes(user).CopyTo(h, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(18), record);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), (ushort)data.Length);
        data.CopyTo(h, 54);
        return h;
    }
}
