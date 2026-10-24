using System.Text.RegularExpressions;

namespace PlayMap.Core.Machine;

/// <summary>PlayMap's published GPU policy (<c>docs/INSTALL.md</c>), pure: NVIDIA RTX and RTX PRO tier 1; AMD's RDNA 3 and 4 Radeons
/// validated per driver; Intel Arc best effort; Microsoft's software adapters (WARP, the Basic Render Driver) and anything else
/// unsupported. RTX PRO is told from RTX by NVAPI's workstation-feature probe, never by the name alone.</summary>
public static partial class VendorPolicy
{
    public const int Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086, Microsoft = 0x1414;

    /// <summary>The tier of an adapter: its vendor id, its name, and whether NVAPI answered that it supports the professional
    /// workstation features (null: not asked, or not NVIDIA).</summary>
    public static VendorTier Tier(int vendorId, string name, bool? workstation)
    {
        ArgumentNullException.ThrowIfNull(name);
        return vendorId switch
        {
            Nvidia when workstation == true => VendorTier.NvidiaRtxPro,
            Nvidia when name.Contains("RTX", StringComparison.OrdinalIgnoreCase) => VendorTier.NvidiaRtx,
            Amd when name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) => VendorTier.Radeon,
            Intel when ArcName().IsMatch(name) => VendorTier.IntelArc,
            Microsoft => VendorTier.Software,
            _ => VendorTier.Other,
        };
    }

    /// <summary>What the published policy promises an adapter of that tier: a Radeon is validated per driver only on RDNA 3 and 4 (told
    /// by its model number), any other Radeon is unsupported.</summary>
    public static SupportLevel Support(VendorTier tier, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return tier switch
        {
            VendorTier.NvidiaRtxPro or VendorTier.NvidiaRtx => SupportLevel.Tier1,
            VendorTier.Radeon => Rdna3Or4().IsMatch(name) ? SupportLevel.ValidatedPerDriver : SupportLevel.Unsupported,
            VendorTier.IntelArc => SupportLevel.BestEffort,
            _ => SupportLevel.Unsupported,
        };
    }

    /// <summary>The words a person reads for a support level.</summary>
    public static string Words(SupportLevel support) => support switch
    {
        SupportLevel.Tier1 => "tier 1: validated",
        SupportLevel.ValidatedPerDriver => "validated per driver: a driver outside the validated list is not validated (no driver is validated before the lab runs)",
        SupportLevel.BestEffort => "best effort",
        _ => "unsupported",
    };

    [GeneratedRegex(@"\bArc\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ArcName();

    // RDNA 3 and 4: the RX 7000 and 9000 series, Radeon PRO W7000 and W9000, Radeon AI PRO R9000.
    [GeneratedRegex(@"\b(RX\s?[79]\d{3}|PRO\s?W[79]\d{3}|AI\s?PRO\s?R9\d{3})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Rdna3Or4();
}
