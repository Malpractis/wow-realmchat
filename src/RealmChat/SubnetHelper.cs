using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RealmChat
{
    // Derives this machine's LAN subnet(s) from its own adapters at runtime,
    // so nothing environment-specific has to ship in the binary. Only real
    // LAN adapters count (up, IPv4, with a default gateway).
    public static class SubnetHelper
    {
        public static List<string> LocalSubnets()
        {
            var found = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = nic.GetIPProperties();
                    bool hasGateway = false;
                    foreach (var gw in props.GatewayAddresses)
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork) { hasGateway = true; break; }
                    if (!hasGateway) continue;

                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var cidr = ToCidr(addr.Address.GetAddressBytes(),
                                          addr.IPv4Mask == null ? null : addr.IPv4Mask.GetAddressBytes());
                        if (cidr != null && !found.Contains(cidr)) found.Add(cidr);
                    }
                }
            }
            catch { }
            return found;
        }

        // All subnets the firewall rule should allow: this machine's LAN(s)
        // plus the configured server-side subnet(s).
        public static List<string> AllowedSubnets(AppConfig cfg)
        {
            var all = LocalSubnets();
            foreach (var s in cfg.GetServerSubnets())
                if (!all.Contains(s)) all.Add(s);
            all.Sort(StringComparer.Ordinal);   // stable order -> stable rule spec
            return all;
        }

        public static bool LooksLikeCidr(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split('/');
            if (parts.Length != 2) return false;
            System.Net.IPAddress ip;
            int prefix;
            return System.Net.IPAddress.TryParse(parts[0], out ip) &&
                   ip.AddressFamily == AddressFamily.InterNetwork &&
                   int.TryParse(parts[1], out prefix) && prefix >= 8 && prefix <= 32;
        }

        private static string ToCidr(byte[] ip, byte[] mask)
        {
            if (ip == null || mask == null || ip.Length != 4 || mask.Length != 4) return null;
            if (ip[0] == 169 && ip[1] == 254) return null;   // APIPA
            int prefix = 0;
            var net = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                net[i] = (byte)(ip[i] & mask[i]);
                for (int b = 7; b >= 0; b--)
                    if ((mask[i] & (1 << b)) != 0) prefix++;
            }
            if (prefix < 8 || prefix > 30) return null;      // not a plausible LAN
            return net[0] + "." + net[1] + "." + net[2] + "." + net[3] + "/" + prefix;
        }
    }
}
