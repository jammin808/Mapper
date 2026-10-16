using System.Globalization;
using System.Net.NetworkInformation;

namespace PlayMap.Engine.VideoIo;

/// <summary>The network interface sends leave by and the link speed it reports (Mb/s; −1 when it reports none, as a virtual NIC
/// does). The named interface, or the first one up that is not loopback.</summary>
internal readonly record struct NicInfo(string Name, long LinkMbps)
{
    public static NicInfo Read(string preferred)
    {
        try
        {
            NetworkInterface? chosen = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (preferred.Length > 0)
                {
                    if (string.Equals(nic.Name, preferred, StringComparison.Ordinal))
                    {
                        chosen = nic;
                        break;
                    }
                    continue;
                }
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    chosen = nic;
                    break;
                }
            }
            if (chosen is null)
                return new NicInfo(preferred.Length > 0 ? preferred : "", -1);
            return new NicInfo(chosen.Name, SpeedOf(chosen));
        }
        catch (NetworkInformationException)
        {
            return new NicInfo(preferred, -1);
        }
    }

    private static long SpeedOf(NetworkInterface nic)
    {
        // Linux reports the negotiated speed in sysfs (-1 or absent for a virtual link); elsewhere the platform's own figure.
        if (OperatingSystem.IsLinux())
        {
            var path = Path.Combine("/sys/class/net", nic.Name, "speed");
            try
            {
                return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mbps) && mbps > 0
                    ? mbps
                    : -1;
            }
            catch (IOException)
            {
                return -1;
            }
        }
        var speed = nic.Speed;
        return speed > 0 ? speed / 1_000_000 : -1;
    }
}
