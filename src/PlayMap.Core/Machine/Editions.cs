using System.Collections.Immutable;
using System.Globalization;

namespace PlayMap.Core.Machine;

/// <summary>The support tier an edition gives a node (<c>docs/INSTALL.md</c>'s three, D4): IoT Enterprise LTSC for permanent show
/// nodes, Pro, Workstation, Enterprise and Education (managed with update policies), Home (supported, unmanaged); a build below 24H2
/// or a non-client edition is unsupported, an edition never read is unknown.</summary>
public enum SupportTier
{
    Unknown,
    Ltsc,
    Managed,
    Home,
    Unsupported,
}

/// <summary>How Windows names its editions, pure: <c>EditionID</c> (the registry's, read first) and <c>GetProductInfo</c>'s product
/// type (when the edition id is absent or one this table does not know) to the edition family with its LTSC and IoT flags, and the
/// family's support tier. A product type or edition id the table does not know is <see cref="EditionFamily.Unknown"/>, never guessed.</summary>
public static class EditionTable
{
    /// <summary>The first build of Windows 11 24H2, the oldest PlayMap supports (D4).</summary>
    public const int MinBuild = 26100;

    private static readonly Dictionary<string, (EditionFamily Family, bool Ltsc, bool Iot)> ByEditionId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"] = (EditionFamily.Home, false, false),
        ["CoreN"] = (EditionFamily.Home, false, false),
        ["CoreSingleLanguage"] = (EditionFamily.Home, false, false),
        ["CoreCountrySpecific"] = (EditionFamily.Home, false, false),
        ["Professional"] = (EditionFamily.Pro, false, false),
        ["ProfessionalN"] = (EditionFamily.Pro, false, false),
        ["ProfessionalWorkstation"] = (EditionFamily.Workstation, false, false),
        ["ProfessionalWorkstationN"] = (EditionFamily.Workstation, false, false),
        ["ProfessionalEducation"] = (EditionFamily.Education, false, false),
        ["ProfessionalEducationN"] = (EditionFamily.Education, false, false),
        ["Enterprise"] = (EditionFamily.Enterprise, false, false),
        ["EnterpriseN"] = (EditionFamily.Enterprise, false, false),
        ["EnterpriseS"] = (EditionFamily.Enterprise, true, false),
        ["EnterpriseSN"] = (EditionFamily.Enterprise, true, false),
        ["Education"] = (EditionFamily.Education, false, false),
        ["EducationN"] = (EditionFamily.Education, false, false),
        ["IoTEnterprise"] = (EditionFamily.Iot, false, true),
        ["IoTEnterpriseK"] = (EditionFamily.Iot, false, true),
        ["IoTEnterpriseS"] = (EditionFamily.Iot, true, true),
        ["IoTEnterpriseSK"] = (EditionFamily.Iot, true, true),
        ["ServerStandard"] = (EditionFamily.Server, false, false),
        ["ServerDatacenter"] = (EditionFamily.Server, false, false),
        ["ServerStandardCore"] = (EditionFamily.Server, false, false),
        ["ServerDatacenterCore"] = (EditionFamily.Server, false, false),
        ["ServerAzureEdition"] = (EditionFamily.Server, false, false),
        ["ServerDatacenterACor"] = (EditionFamily.Server, false, false),
        ["ServerStandardACor"] = (EditionFamily.Server, false, false),
    };

    private static readonly Dictionary<uint, (EditionFamily Family, bool Ltsc, bool Iot)> ByProductType = new()
    {
        [0x65] = (EditionFamily.Home, false, false),        // PRODUCT_CORE
        [0x62] = (EditionFamily.Home, false, false),        // PRODUCT_CORE_N
        [0x63] = (EditionFamily.Home, false, false),        // PRODUCT_CORE_COUNTRYSPECIFIC
        [0x64] = (EditionFamily.Home, false, false),        // PRODUCT_CORE_SINGLELANGUAGE
        [0x30] = (EditionFamily.Pro, false, false),         // PRODUCT_PROFESSIONAL
        [0x31] = (EditionFamily.Pro, false, false),         // PRODUCT_PROFESSIONAL_N
        [0xA1] = (EditionFamily.Workstation, false, false), // PRODUCT_PRO_WORKSTATION
        [0xA2] = (EditionFamily.Workstation, false, false), // PRODUCT_PRO_WORKSTATION_N
        [0xA4] = (EditionFamily.Education, false, false),   // PRODUCT_PRO_FOR_EDUCATION
        [0xA5] = (EditionFamily.Education, false, false),   // PRODUCT_PRO_FOR_EDUCATION_N
        [0x04] = (EditionFamily.Enterprise, false, false),  // PRODUCT_ENTERPRISE
        [0x1B] = (EditionFamily.Enterprise, false, false),  // PRODUCT_ENTERPRISE_N
        [0x7D] = (EditionFamily.Enterprise, true, false),   // PRODUCT_ENTERPRISE_S
        [0x7E] = (EditionFamily.Enterprise, true, false),   // PRODUCT_ENTERPRISE_S_N
        [0x79] = (EditionFamily.Education, false, false),   // PRODUCT_EDUCATION
        [0x7A] = (EditionFamily.Education, false, false),   // PRODUCT_EDUCATION_N
        [0xBC] = (EditionFamily.Iot, false, true),          // PRODUCT_IOTENTERPRISE
        [0xBF] = (EditionFamily.Iot, true, true),           // PRODUCT_IOTENTERPRISES
        [0x07] = (EditionFamily.Server, false, false),      // PRODUCT_STANDARD_SERVER
        [0x08] = (EditionFamily.Server, false, false),      // PRODUCT_DATACENTER_SERVER
        [0x0C] = (EditionFamily.Server, false, false),      // PRODUCT_DATACENTER_SERVER_CORE
        [0x0D] = (EditionFamily.Server, false, false),      // PRODUCT_STANDARD_SERVER_CORE
        [0x91] = (EditionFamily.Server, false, false),      // PRODUCT_DATACENTER_A_SERVER_CORE
        [0x92] = (EditionFamily.Server, false, false),      // PRODUCT_STANDARD_A_SERVER_CORE
    };

    /// <summary>The operating system from what Windows answered: the edition id (registry <c>EditionID</c>, "" when absent), the product
    /// type (<c>GetProductInfo</c>, 0 when not asked), the display version ("24H2"), the build and its revision (<c>UBR</c>).</summary>
    public static OsIdentity Classify(string editionId, uint productType, string displayVersion, int build, int revision)
    {
        var id = editionId?.Trim() ?? "";
        var known = ByEditionId.TryGetValue(id, out var byId) ? byId
            : ByProductType.TryGetValue(productType, out var byType) ? byType
            : (EditionFamily.Unknown, false, false);
        var edition = id.Length > 0 ? id : productType == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"product 0x{productType:X}");
        var version = displayVersion?.Trim() is { Length: > 0 } v ? v : VersionOfBuild(build);
        return new OsIdentity(edition, known.Item1, version, build, revision, known.Item2, known.Item3);
    }

    /// <summary>The version a client build is ("23H2", "24H2", "25H2"), "" for a build this table does not name.</summary>
    public static string VersionOfBuild(int build) => build switch
    {
        22631 => "23H2",
        26100 => "24H2",
        26200 => "25H2",
        _ => "",
    };

    /// <summary>The support tier the operating system gives this node.</summary>
    public static SupportTier Tier(OsIdentity os)
    {
        ArgumentNullException.ThrowIfNull(os);
        if (os.Family == EditionFamily.Unknown)
            return SupportTier.Unknown;
        if (os.Family == EditionFamily.Server || (os.Build > 0 && os.Build < MinBuild))
            return SupportTier.Unsupported;
        if (os.Family == EditionFamily.Iot && os.Ltsc)
            return SupportTier.Ltsc;
        return os.Family == EditionFamily.Home ? SupportTier.Home : SupportTier.Managed;
    }

    /// <summary>A tier's word in STATE: "ltsc", "managed", "home", "unsupported", "unknown".</summary>
    public static string Word(SupportTier tier) => tier switch
    {
        SupportTier.Ltsc => "ltsc",
        SupportTier.Managed => "managed",
        SupportTier.Home => "home",
        SupportTier.Unsupported => "unsupported",
        _ => "unknown",
    };

    /// <summary>The edition as a person reads it: "Windows 11 Home", "Windows 11 IoT Enterprise LTSC", "Windows Server".</summary>
    public static string Name(OsIdentity os)
    {
        ArgumentNullException.ThrowIfNull(os);
        var family = os.Family switch
        {
            EditionFamily.Home => "Home",
            EditionFamily.Pro => "Pro",
            EditionFamily.Workstation => "Pro for Workstations",
            EditionFamily.Enterprise => os.Ltsc ? "Enterprise LTSC" : "Enterprise",
            EditionFamily.Education => "Education",
            EditionFamily.Iot => os.Ltsc ? "IoT Enterprise LTSC" : "IoT Enterprise",
            _ => "",
        };
        if (os.Family == EditionFamily.Server)
            return "Windows Server";
        return family.Length == 0 ? (os.Edition.Length == 0 ? "Windows (edition unknown)" : "Windows " + os.Edition) : "Windows 11 " + family;
    }
}

/// <summary>When a version of an edition stops receiving security updates, as Microsoft's lifecycle publishes it, shipped in the build
/// and dated with it (<see cref="TableDate"/>): Home, Pro, Pro for Workstations and Pro Education versions get 24 months,
/// Enterprise, Education and IoT Enterprise versions 36 months, and the 2024 LTSC releases their own dates. A version the table does
/// not name answers null ("servicing unknown"), never a guess.</summary>
public static class ServicingTable
{
    /// <summary>The day this table was last read against the published lifecycle.</summary>
    public static readonly DateOnly TableDate = new(2026, 10, 19);

    private static readonly Dictionary<string, (DateOnly Consumer, DateOnly Commercial)> GeneralAvailability = new(StringComparer.OrdinalIgnoreCase)
    {
        ["23H2"] = (new DateOnly(2025, 11, 11), new DateOnly(2026, 11, 10)),
        ["24H2"] = (new DateOnly(2026, 10, 13), new DateOnly(2027, 10, 12)),
        ["25H2"] = (new DateOnly(2027, 10, 12), new DateOnly(2028, 10, 10)),
        ["26H2"] = (new DateOnly(2028, 10, 10), new DateOnly(2029, 10, 9)),
    };

    /// <summary>The last day a version of this edition is serviced, or null when the table does not name it.</summary>
    public static DateOnly? EndOf(OsIdentity os)
    {
        ArgumentNullException.ThrowIfNull(os);
        if (os.Ltsc)
        {
            // The 2024 LTSC releases (build 26100): Enterprise LTSC five years, IoT Enterprise LTSC ten.
            if (os.Build != 26100)
                return null;
            return os.Iot ? new DateOnly(2034, 10, 10) : new DateOnly(2029, 10, 9);
        }
        if (!GeneralAvailability.TryGetValue(os.Version, out var end))
            return null;
        return os.Family switch
        {
            EditionFamily.Home or EditionFamily.Pro or EditionFamily.Workstation => end.Consumer,
            EditionFamily.Enterprise or EditionFamily.Education or EditionFamily.Iot =>
                // Pro Education is serviced as Pro.
                os.Edition.StartsWith("ProfessionalEducation", StringComparison.OrdinalIgnoreCase) ? end.Consumer : end.Commercial,
            _ => null,
        };
    }

    /// <summary>The oldest version this table still services for the edition on <paramref name="today"/>, or "" when none it names is.</summary>
    public static string OldestServiced(OsIdentity os, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(os);
        foreach (var version in GeneralAvailability.Keys.Order(StringComparer.Ordinal))
            if (EndOf(os with { Version = version, Ltsc = false }) is { } end && end >= today)
                return version;
        return "";
    }

    public static ImmutableArray<string> Versions => [.. GeneralAvailability.Keys.Order(StringComparer.Ordinal)];
}
