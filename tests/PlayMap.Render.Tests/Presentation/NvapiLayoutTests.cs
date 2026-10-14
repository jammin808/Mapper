using System.Runtime.InteropServices;
using PlayMap.Render.Sync;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The NVAPI structures' sizes and version words equal the driver header's (MIT nvapi.h: MAKE_NVAPI_VERSION(T, v) =
/// sizeof(T) | v &lt;&lt; 16, x64 layout): the tier itself is compile-only here, so its layouts are what this machine proves.</summary>
public sealed class NvapiLayoutTests
{
    [Fact]
    public unsafe void StructSizesAndVersionWordsEqualTheHeaders()
    {
        Assert.Equal(16, sizeof(NvGSyncStatus));
        Assert.Equal(0x00010010u, NvGSyncStatus.Ver);
        Assert.Equal(36, sizeof(NvGSyncStatusParams));
        Assert.Equal(0x00020024u, NvGSyncStatusParams.Ver);
        Assert.Equal(28, sizeof(NvGSyncCapabilities));
        Assert.Equal(0x0003001Cu, NvGSyncCapabilities.Ver);
        Assert.Equal(40, sizeof(NvGSyncGpu));
        Assert.Equal(0x00010028u, NvGSyncGpu.Ver);
        Assert.Equal(96, sizeof(NvGSyncDisplay));
        Assert.Equal(0x00020060u, NvGSyncDisplay.Ver);
        Assert.Equal(Marshal.SizeOf<NvGSyncGpu>(), sizeof(NvGSyncGpu));
        // The pointer-sized handles sit on 8-byte boundaries (a positional record's fields are its properties' backing fields).
        Assert.Equal(8, (int)Marshal.OffsetOf<NvGSyncGpu>(Field(nameof(NvGSyncGpu.PhysicalGpu))));
        Assert.Equal(24, (int)Marshal.OffsetOf<NvGSyncGpu>(Field(nameof(NvGSyncGpu.ProxyPhysicalGpu))));
        Assert.Equal(32, (int)Marshal.OffsetOf<NvGSyncGpu>(Field(nameof(NvGSyncGpu.SyncedBits))));
        Assert.Equal(16, (int)Marshal.OffsetOf<NvGSyncStatusParams>(Field(nameof(NvGSyncStatusParams.Rj45Ethernet0))));
    }

    private static string Field(string property) => $"<{property}>k__BackingField";

    [Fact]
    public void InterfaceIdsAreTheHeadersOwn()
    {
        Assert.Equal(0x0150e828u, NvApiIds.Initialize);
        Assert.Equal(0xd9639601u, NvApiIds.GSyncEnumSyncDevices);
        Assert.Equal(0x44a3f1d1u, NvApiIds.GSyncQueryCapabilities);
        Assert.Equal(0x4562bc38u, NvApiIds.GSyncGetTopology);
        Assert.Equal(0xf1f5b434u, NvApiIds.GSyncGetSyncStatus);
        Assert.Equal(0x70d404ecu, NvApiIds.GSyncGetStatusParameters);
        Assert.Equal(0x9152e055u, NvApiIds.D3D1xQueryFrameCount);
        Assert.Equal(0x14610cd7u, NvApiIds.D3D1xJoinSwapGroup);
        Assert.Equal(0x9de8c729u, NvApiIds.D3D1xBindSwapBarrier);
        Assert.Equal(-108, NvApiIds.DeviceBusy);
    }

    /// <summary>On the Windows CI runner (no NVIDIA driver) the tier says so with words; here (Linux) it says NVAPI is Windows only.
    /// Either way the node gets a tier with no board, and every output presents free.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void TheMachinesTierSaysWhyItHasNoBoard()
    {
        using var tier = FrameLockDrivers.ForThisMachine();
        if (tier.Name == "nvapi")
        {
            // A machine with an NVIDIA driver: the tier loaded; a board is there or not, and no GL output joins a swap group.
            Assert.False(tier.Join("op_1", null, 1, 1, out var words));
            Assert.Contains("DXGI", words, StringComparison.Ordinal);
            return;
        }
        Assert.Equal("none", tier.Name);
        Assert.Null(tier.Board());
        Assert.Equal(0, tier.MaxSwapGroups);
        if (OperatingSystem.IsWindows())
            Assert.Contains("NVIDIA", tier.Words, StringComparison.Ordinal);
        else
            Assert.Equal("NVAPI is Windows only: outputs present free, measured", tier.Words);
        Assert.False(tier.Join("op_1", null, 1, 1, out var refused));
        Assert.Equal(tier.Words, refused);
    }
}
