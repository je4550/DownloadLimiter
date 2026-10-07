using System.Net.NetworkInformation;
using System.Net.Sockets;
using DownloadLimit.Core;

namespace DownloadLimit.Windows;

public static class LocalNetworks
{
    public static NetworkClassifier Snapshot()
    {
        var prefixes = new List<NetworkPrefix>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    continue;
                int bits = address.Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                // A /0 is never a local-network exclusion, including on VPN adapters.
                if (address.PrefixLength is > 0 && address.PrefixLength <= bits)
                    prefixes.Add(NetworkPrefix.Create(address.Address, address.PrefixLength));
            }
        }
        return new NetworkClassifier(prefixes);
    }
}
