using System.Globalization;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Engine.Wall;

/// <summary>How a point on the wall maps to a projector's raster: the output that shows the projector (its final raster is the
/// wall's pixels), its warp, its quarter turns and the raster's size. Wall-normalised is (0, 0) at the output's top-left, 1 across;
/// raster-normalised is the projector picture's own (0, 0)–(1, 1) before the output's warp. With no output, only raster
/// coordinates mean anything.</summary>
internal sealed class WallFrame
{
    private readonly WarpField _field;

    private WallFrame(Projector projector, Output? output)
    {
        Projector = projector;
        Output = output;
        Width = output?.Width ?? projector.Intrinsics.Width;
        Height = output?.Height ?? projector.Intrinsics.Height;
        _field = output is null ? WarpField.Identity : new WarpField(output.Warp);
        Quarters = output is null ? 0 : (int)(((Math.Round(output.Rotation / 90.0) % 4) + 4) % 4);
    }

    public Projector Projector { get; }

    /// <summary>The output that puts this projector on the wall, or null when none does.</summary>
    public Output? Output { get; }

    /// <summary>The raster's (and the wall's) pixels across and down.</summary>
    public int Width { get; }

    public int Height { get; }

    public int Quarters { get; }

    public string Where => Output?.Name ?? Projector.Name;

    /// <summary>The frame of a projector: the first enabled output in projector mode that shows it, else the first that shows it.</summary>
    public static WallFrame Of(Project project, Projector projector)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(projector);
        Output? shows = null;
        foreach (var o in project.Outputs)
        {
            if (o.Mode != OutputMode.Projector || !string.Equals(o.ProjectorId, projector.Id, StringComparison.Ordinal))
                continue;
            if (o.Enabled)
                return new WallFrame(projector, o);
            shows ??= o;
        }
        return new WallFrame(projector, shows);
    }

    public static WallFrame Of(Projector projector, Output output) => new(projector, output);

    /// <summary>A wall-normalised point on the projector's raster (raster-normalised); null when no point of the warped picture
    /// lands there.</summary>
    public Vec2d? ToRaster(Vec2d wall) => _field.Invert(Unturn(wall).X, Unturn(wall).Y);

    /// <summary>Where a raster-normalised point lands on the wall (wall-normalised).</summary>
    public Vec2d ToWall(Vec2d raster)
    {
        var a = _field.Evaluate(raster.X, raster.Y);
        return Quarters switch
        {
            1 => new Vec2d(1 - a.Y, a.X),
            2 => new Vec2d(1 - a.X, 1 - a.Y),
            3 => new Vec2d(a.Y, 1 - a.X),
            _ => a,
        };
    }

    /// <summary>A move of <paramref name="wallPx"/> wall pixels at raster point <paramref name="at"/>, as a raster-normalised move
    /// (the warp's local inverse there); null where the warp folds.</summary>
    public Vec2d? WallDeltaToRaster(Vec2d at, Vec2d wallPx)
    {
        var df = new Vec2d(wallPx.X / Width, wallPx.Y / Height);
        // The output turns its picture clockwise by quarters; a move on the wall turns back the other way.
        var (fx, fy) = (df.X, df.Y);
        var da = Quarters switch
        {
            1 => new Vec2d(fy, -fx),
            2 => new Vec2d(-fx, -fy),
            3 => new Vec2d(-fy, fx),
            _ => df,
        };
        var (du, dv) = _field.Jacobian(at);
        var det = du.X * dv.Y - dv.X * du.Y;
        if (Math.Abs(det) < 1e-12)
            return null;
        return new Vec2d((dv.Y * da.X - dv.X * da.Y) / det, (-du.Y * da.X + du.X * da.Y) / det);
    }

    private Vec2d Unturn(Vec2d f) => Quarters switch
    {
        1 => new Vec2d(f.Y, 1 - f.X),
        2 => new Vec2d(1 - f.X, 1 - f.Y),
        3 => new Vec2d(1 - f.Y, f.X),
        _ => f,
    };

    /// <summary>A raster-normalised point in raster pixels, as words ("812.0, 240.5").</summary>
    public string Px(Vec2d raster) => string.Create(CultureInfo.InvariantCulture, $"{raster.X * Width:0.0}, {raster.Y * Height:0.0}");
}

/// <summary>The units a wall verb's numbers are in.</summary>
internal enum WallUnits
{
    /// <summary>The output's final pixels (the wall).</summary>
    WallPx,
    /// <summary>0–1 across and down the output.</summary>
    WallNormalised,
    /// <summary>The projector raster's pixels, before the output's warp.</summary>
    RasterPx,
}
