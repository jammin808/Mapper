using System.IO.Compression;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Align;

/// <summary>
/// What a run saw of one output, kept beside the run (capture-&lt;op&gt;.bin) so a later ALIGN RUN … ONLY can rescan one output and keep
/// the others: the decode (projector x, y and status per camera pixel), the footprint, the white picture and the photometry, deflated.
/// The camera's black picture of the run is kept the same way (black.bin). Little-endian; a file of another version or shape reads as
/// null.
/// </summary>
public static class AlignCaptureFile
{
    private const int Version = 1;

    public static byte[] Write(AlignCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(z))
        {
            var m = capture.Map;
            w.Write(Version);
            w.Write(capture.OutputId);
            w.Write(capture.Width);
            w.Write(capture.Height);
            w.Write(m.Width);
            w.Write(m.Height);
            w.Write(m.ProjectorWidth);
            w.Write(m.ProjectorHeight);
            w.Write(m.Region.X);
            w.Write(m.Region.Y);
            w.Write(m.Region.Width);
            w.Write(m.Region.Height);
            w.Write((int)m.Axes);
            w.Write(m.SubPixel);
            for (var i = 0; i < m.ProjectorX.Length; i++)
            {
                w.Write(m.ProjectorX[i]);
                w.Write(m.ProjectorY[i]);
                w.Write((byte)m.Flags[i]);
            }
            var f = capture.Footprint;
            w.Write(f.Area);
            w.Write(f.Centroid.X);
            w.Write(f.Centroid.Y);
            w.Write(f.Bounds.X);
            w.Write(f.Bounds.Y);
            w.Write(f.Bounds.Width);
            w.Write(f.Bounds.Height);
            w.Write(f.Threshold);
            foreach (var b in f.Mask)
                w.Write(b);
            w.Write(capture.White.Length);
            w.Write(capture.White);
            var p = capture.Photometry;
            w.Write(p.Black);
            w.Write(p.White);
            w.Write(p.Gamma);
            w.Write(p.GammaMisfit);
            w.Write(p.Samples);
        }
        return ms.ToArray();
    }

    /// <summary>A capture read back, with the prior the next run gives it; null when the bytes are not a capture this code wrote.</summary>
    public static AlignCapture? Read(byte[] bytes, ProjectorPrior prior)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var z = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var r = new BinaryReader(z);
            if (r.ReadInt32() != Version)
                return null;
            var id = r.ReadString();
            var width = r.ReadInt32();
            var height = r.ReadInt32();
            var mw = r.ReadInt32();
            var mh = r.ReadInt32();
            var pw = r.ReadInt32();
            var ph = r.ReadInt32();
            var region = new PixelRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (mw is < 1 or > 16384 || mh is < 1 or > 16384 || pw is < 1 or > 65536 || ph is < 1 or > 65536)
                return null;
            var axes = (ScanAxes)r.ReadInt32();
            var sub = r.ReadBoolean();
            var map = new CorrespondenceMap(mw, mh, pw, ph, region, axes, sub);
            for (var i = 0; i < mw * mh; i++)
            {
                map.ProjectorX[i] = r.ReadSingle();
                map.ProjectorY[i] = r.ReadSingle();
                map.Flags[i] = (DecodeStatus)r.ReadByte();
            }
            map.Recount(0, 0, 0);
            var area = r.ReadInt32();
            var centroid = new Vec2d(r.ReadDouble(), r.ReadDouble());
            var bounds = new PixelRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            var threshold = r.ReadInt32();
            var mask = new bool[mw * mh];
            for (var i = 0; i < mask.Length; i++)
                mask[i] = r.ReadBoolean();
            var whiteLength = r.ReadInt32();
            if (whiteLength != mw * mh)
                return null;
            var white = r.ReadBytes(whiteLength);
            var photometry = new ProjectorPhotometry(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadInt32());
            return new AlignCapture(id, width, height, map, new Footprint(mw, mh, mask, area, centroid, bounds, threshold), white, photometry, prior);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The run's black picture kept (its raster first).</summary>
    public static byte[] WriteBlack(CameraShot black)
    {
        ArgumentNullException.ThrowIfNull(black);
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(z))
        {
            w.Write(Version);
            w.Write(black.Width);
            w.Write(black.Height);
            w.Write(black.Gray);
        }
        return ms.ToArray();
    }

    public static CameraShot? ReadBlack(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var z = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var r = new BinaryReader(z);
            if (r.ReadInt32() != Version)
                return null;
            var w = r.ReadInt32();
            var h = r.ReadInt32();
            if (w is < 1 or > 16384 || h is < 1 or > 16384)
                return null;
            var gray = r.ReadBytes(w * h);
            return gray.Length == w * h ? new CameraShot(gray, w, h) : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
