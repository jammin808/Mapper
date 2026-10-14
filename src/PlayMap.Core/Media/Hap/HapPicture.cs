namespace PlayMap.Core.Media.Hap;

/// <summary>What a HAP lease tells the renderer beyond <see cref="VideoFrameLease"/>: the factor that brings a linear HAP HDR
/// value to SDR reference white (1.0 = 203 nits ⇒ 1). A lease of any other source does not implement it, and the renderer
/// reads 1.</summary>
public interface IHapPicture
{
    double HdrScale { get; }
}
