using System.Runtime.Versioning;

namespace PlayMap.Platform.Windows;

/// <summary>The Windows machine platform's project. It claims nothing yet: no node composes it, so a hosted node's services carry
/// Core's null platform, which claims nothing and says so, and a thread claim made outside a host answers
/// ThreadClaims.NoPlatformWords.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPlatformProject
{
    // wp3-platform fills this.

    /// <summary>Why no claim is made of Windows on this node.</summary>
    public static string Words => "the Windows machine platform is not built yet: no claim is made of Windows";
}
