using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public class VpnAdapterInfo
{
    public int InterfaceIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IPAddress? IpAddress { get; set; }
    public IPAddress? SubnetMask { get; set; }
    public IPAddress? Gateway { get; set; }
    public OperationalStatus Status { get; set; }
}

public class InterfaceBindingService
{
    private const int IPPROTO_IP = 0;
    private const int IP_UNICAST_IF = 31;
    private const int IPPROTO_IPV6 = 41;
    private const int IPV6_UNICAST_IF = 31;

    public static List<VpnAdapterInfo> GetAllAdapters()
    {
        var list = new List<VpnAdapterInfo>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                var ipv4Props = ipProps.GetIPv4Properties();
                int ifIdx = ipv4Props?.Index ?? 0;

                IPAddress? ipv4Addr = null;
                IPAddress? mask = null;

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ipv4Addr = unicast.Address;
                        mask = unicast.IPv4Mask;
                        break;
                    }
                }

                IPAddress? gw = null;
                foreach (var g in ipProps.GatewayAddresses)
                {
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        gw = g.Address;
                        break;
                    }
                }

                list.Add(new VpnAdapterInfo
                {
                    InterfaceIndex = ifIdx,
                    Name = ni.Name,
                    Description = ni.Description,
                    IpAddress = ipv4Addr,
                    SubnetMask = mask,
                    Gateway = gw,
                    Status = ni.OperationalStatus
                });
            }
        }
        catch { }

        return list;
    }

    public static VpnAdapterInfo? FindVpnAdapter(string transportType = "OpenVPN", string? preferredName = null)
    {
        var adapters = GetAllAdapters();

        // 1. Search by exact preferred name if provided
        if (!string.IsNullOrEmpty(preferredName))
        {
            var match = adapters.FirstOrDefault(a => a.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // 2. Search for DCO / OpenVPN / Wintun / WireGuard / TAP keywords in adapter description or name
        var vpnKeywords = new[] { "dco", "ovpn", "openvpn", "wintun", "wireguard", "tap-windows", "tap", "dcss" };
        foreach (var keyword in vpnKeywords)
        {
            var match = adapters.FirstOrDefault(a =>
                (a.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                 a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
                a.IpAddress != null &&
                !a.IpAddress.Equals(IPAddress.Loopback) &&
                !a.IpAddress.Equals(IPAddress.Any));

            if (match != null) return match;
        }

        return null;
    }

    public static bool BindSocketToInterface(Socket socket, int interfaceIndex, IPAddress? interfaceIp = null)
    {
        try
        {
            if (interfaceIndex <= 0) return false;

            if (socket.AddressFamily == AddressFamily.InterNetwork)
            {
                // Convert interface index to network byte order for IP_UNICAST_IF
                int netIfIndex = IPAddress.HostToNetworkOrder(interfaceIndex);
                byte[] optVal = BitConverter.GetBytes(netIfIndex);
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_UNICAST_IF, optVal);

                if (interfaceIp != null && !interfaceIp.Equals(IPAddress.Any))
                {
                    try
                    {
                        socket.Bind(new IPEndPoint(interfaceIp, 0));
                    }
                    catch { }
                }

                return true;
            }
            else if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                int netIfIndex = IPAddress.HostToNetworkOrder(interfaceIndex);
                byte[] optVal = BitConverter.GetBytes(netIfIndex);
                socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)IPV6_UNICAST_IF, optVal);
                return true;
            }
        }
        catch { }

        return false;
    }
}
