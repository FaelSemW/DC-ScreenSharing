using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DCScreenSharing.Core.Profiles;

public class ParsedWireGuardConfig
{
    public string PrivateKey { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public string Address
    {
        get => Addresses.Count > 0 ? string.Join(", ", Addresses) : string.Empty;
        set => Addresses = WireGuardConfParser.ParseCidrList(value);
    }
    public List<string> DnsServers { get; set; } = new();
    public string Dns
    {
        get => DnsServers.Count > 0 ? string.Join(", ", DnsServers) : string.Empty;
        set => DnsServers = WireGuardConfParser.ParseDnsList(value);
    }
    public int Mtu { get; set; } = 1420;
    public string PeerPublicKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public int Port { get; set; } = 51820;
    public List<string> AllowedIpsList { get; set; } = new();
    public string AllowedIps
    {
        get => AllowedIpsList.Count > 0 ? string.Join(", ", AllowedIpsList) : string.Empty;
        set => AllowedIpsList = WireGuardConfParser.ParseCidrList(value);
    }
    public int PersistentKeepalive { get; set; } = 25;
    public string? PresharedKey { get; set; }
}

public static class WireGuardConfParser
{
    public static ParsedWireGuardConfig Parse(string confContent)
    {
        var result = new ParsedWireGuardConfig();
        var currentSection = string.Empty;

        var lines = confContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            if (string.Equals(currentSection, "Interface", StringComparison.OrdinalIgnoreCase))
            {
                switch (key.ToLowerInvariant())
                {
                    case "privatekey":
                        result.PrivateKey = value;
                        break;
                    case "address":
                        result.Addresses = ParseCidrList(value);
                        break;
                    case "dns":
                        result.DnsServers = ParseDnsList(value);
                        break;
                    case "mtu":
                        if (int.TryParse(value, out var mtu)) result.Mtu = mtu;
                        break;
                }
            }
            else if (string.Equals(currentSection, "Peer", StringComparison.OrdinalIgnoreCase))
            {
                switch (key.ToLowerInvariant())
                {
                    case "publickey":
                        result.PeerPublicKey = value;
                        break;
                    case "endpoint":
                        ParseEndpoint(value, result);
                        break;
                    case "allowedips":
                        result.AllowedIpsList = ParseCidrList(value);
                        break;
                    case "persistentkeepalive":
                        if (int.TryParse(value, out var keepalive)) result.PersistentKeepalive = keepalive;
                        break;
                    case "presharedkey":
                        result.PresharedKey = value;
                        break;
                }
            }
        }

        return result;
    }

    public static List<string> ParseCidrList(string rawCidrs)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(rawCidrs)) return list;
        var parts = rawCidrs.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (IsValidCidr(trimmed, out var norm))
            {
                list.Add(norm);
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                list.Add(trimmed);
            }
        }
        return list;
    }

    public static List<string> ParseDnsList(string rawDns)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(rawDns)) return list;
        var parts = rawDns.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (IsValidDns(trimmed, out var norm))
            {
                list.Add(norm);
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                list.Add(trimmed);
            }
        }
        return list;
    }

    public static bool IsValidCidr(string input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();

        var slashIdx = trimmed.IndexOf('/');
        if (slashIdx >= 0)
        {
            var ipStr = trimmed.Substring(0, slashIdx).Trim();
            var prefixStr = trimmed.Substring(slashIdx + 1).Trim();
            if (IPAddress.TryParse(ipStr, out var ip) && int.TryParse(prefixStr, out var prefix))
            {
                int maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                if (prefix >= 0 && prefix <= maxPrefix)
                {
                    normalized = $"{ipStr}/{prefix}";
                    return true;
                }
            }
            return false;
        }
        else
        {
            if (IPAddress.TryParse(trimmed, out var ip))
            {
                int defaultPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                normalized = $"{trimmed}/{defaultPrefix}";
                return true;
            }
            return false;
        }
    }

    public static bool IsValidDns(string input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();
        if (IPAddress.TryParse(trimmed, out var ip))
        {
            normalized = ip.ToString();
            return true;
        }
        if (Uri.CheckHostName(trimmed) != UriHostNameType.Unknown)
        {
            normalized = trimmed;
            return true;
        }
        return false;
    }

    private static void ParseEndpoint(string endpointValue, ParsedWireGuardConfig config)
    {
        endpointValue = endpointValue.Trim();
        if (endpointValue.StartsWith('['))
        {
            var matchWithPort = Regex.Match(endpointValue, @"^\[([a-fA-F0-9:]+)\]:(\d+)$");
            if (matchWithPort.Success)
            {
                config.Endpoint = matchWithPort.Groups[1].Value;
                if (int.TryParse(matchWithPort.Groups[2].Value, out var port)) config.Port = port;
                return;
            }

            var matchNoPort = Regex.Match(endpointValue, @"^\[([a-fA-F0-9:]+)\]$");
            if (matchNoPort.Success)
            {
                config.Endpoint = matchNoPort.Groups[1].Value;
                config.Port = 51820;
                return;
            }
        }

        var colonIdx = endpointValue.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < endpointValue.Length - 1)
        {
            var hostPart = endpointValue.Substring(0, colonIdx);
            var portPart = endpointValue.Substring(colonIdx + 1);
            if (int.TryParse(portPart, out var port))
            {
                config.Endpoint = hostPart;
                config.Port = port;
                return;
            }
        }

        config.Endpoint = endpointValue;
        config.Port = 51820;
    }
}
