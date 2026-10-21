using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The platform's own capability probes, pure (both jobs): DXGI's failure is a probe that says why no adapter was read, and
/// NVAPI's only where an NVIDIA adapter is there to ask; hardware scheduling is one probe per adapter by its LUID with its driver, from
/// the driver's own two flags; the timer requests are honoured only where the QoS claim asked HIGH, applied and reads back high.</summary>
public sealed class CapabilityTests
{
    private static AdapterIdentity Adapter(int vendor, string name, long luid = 0x123a4) =>
        new(luid, vendor, 0x2bb1, name, "582.16", "", VendorTier.Other, SupportLevel.Unsupported, true, 8192);

    [Fact]
    public void DxgisFailureSaysWhyNoAdapterIsReadAndNvapisOnlyWhereAnNvidiaAdapterIsThere()
    {
        Assert.Empty(MachineCapabilities.Unread("", "", [Adapter(VendorPolicy.Nvidia, "NVIDIA RTX PRO 4000 Blackwell")]));

        var dxgi = Assert.Single(MachineCapabilities.Unread("DXGI 1.6 did not open (0x887A0004): no adapter is read", "", []));
        Assert.Equal((MachineCapabilities.Adapters, 0L, false, "DXGI 1.6 did not open (0x887A0004): no adapter is read"),
            (dxgi.Feature, dxgi.AdapterLuid, dxgi.Supported, dxgi.Words));

        // An AMD machine has no NVIDIA driver: no word of NVAPI.
        Assert.Empty(MachineCapabilities.Unread("", "no NVIDIA driver on this machine (nvapi64.dll is not there)", [Adapter(VendorPolicy.Amd, "AMD Radeon RX 7900 XTX")]));
        // An NVIDIA machine whose NVAPI refused: the RTX PRO probe fell back to the name, and the probe says so.
        var nvapi = Assert.Single(MachineCapabilities.Unread("", "the NVIDIA driver did not start NVAPI", [Adapter(VendorPolicy.Nvidia, "NVIDIA RTX PRO 4000 Blackwell")]));
        Assert.Equal((MachineCapabilities.NvApi, false), (nvapi.Feature, nvapi.Supported));
        Assert.Equal("the NVIDIA driver did not start NVAPI: the RTX PRO probe and the driver's branch are unread", nvapi.Words);
    }

    [Fact]
    public void HardwareSchedulingIsOneProbePerAdapterFromItsDriversFlags()
    {
        var adapter = Adapter(VendorPolicy.Nvidia, "NVIDIA RTX PRO 4000 Blackwell", 0x5566);
        var on = MachineCapabilities.HagsOf(adapter, supported: true, enabled: true);
        Assert.Equal((MachineCapabilities.Hags, 0x5566L, true, "hardware-accelerated GPU scheduling is on", "582.16"),
            (on.Feature, on.AdapterLuid, on.Supported, on.Words, on.Driver));
        var off = MachineCapabilities.HagsOf(adapter, supported: true, enabled: false);
        Assert.Equal((true, "hardware-accelerated GPU scheduling is supported and off"), (off.Supported, off.Words));
        var none = MachineCapabilities.HagsOf(adapter, supported: false, enabled: false);
        Assert.Equal((false, "this adapter's driver does not support hardware-accelerated GPU scheduling"), (none.Supported, none.Words));
    }

    [Fact]
    public void TimerRequestsAreHonouredOnlyWhereTheQosClaimReadsBackHigh()
    {
        Assert.True(MachineCapabilities.TimerHonoured(new Claim("process.qos", "high", true, "high", "")).Supported);

        var unread = MachineCapabilities.TimerHonoured(new Claim("process.qos", "high", true, "unread", "set; Windows does not read it back here (error 87)"));
        Assert.Equal((false, "set; Windows does not read it back here"), (unread.Supported, unread.Words));
        var other = MachineCapabilities.TimerHonoured(new Claim("process.qos", "high", true, "control 0x1 state 0x0", "Windows reads back control 0x1 state 0x0"));
        Assert.Equal((false, "Windows reads back control 0x1 state 0x0"), (other.Supported, other.Words));
        var refused = MachineCapabilities.TimerHonoured(new Claim("process.qos", "high", false, "", "Windows refused the power throttling state: error 5"));
        Assert.Equal((false, "Windows refused the power throttling state: error 5"), (refused.Supported, refused.Words));
        var auto = MachineCapabilities.TimerHonoured(new Claim("process.qos", "auto", true, "auto", ""));
        Assert.Equal((false, "QoS is AUTO: Windows may ignore the timer requests of a hidden process"), (auto.Supported, auto.Words));
        Assert.Equal((false, "no process claim has been made yet"), (MachineCapabilities.TimerHonoured(null).Supported, MachineCapabilities.TimerHonoured(null).Words));
    }
}
