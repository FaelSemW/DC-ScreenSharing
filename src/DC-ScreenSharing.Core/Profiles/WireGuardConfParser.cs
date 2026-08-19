using System.Text.RegularExpressions;

namespace DCScreenSharing.Core.Profiles;

public class ParsedWireGuardConfig
{
    public string PrivateKey { get; set; } = string.Empty;
    public string Address { get; set; } = "10.8.0.2/32";
    public string Dns { get; set; } = "1.1.1.1, 8.8.8.8";
    public int Mtu { get; set; } = 1420;
    public string PeerPublicKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public int Port { get; set; } = 51820;
    public string AllowedIps { get; set; } = "0.0.0.0/0, ::/0";
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
                        result.Address = value;
                        break;
                    case "dns":
                        result.Dns = value;
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
                        result.AllowedIps = value;
                        break;
                }
            }
        }

        return result;
    }

    private static void ParseEndpoint(string endpointValue, ParsedWireGuardConfig config)
    {
        if (endpointValue.StartsWith('['))
        {
            var match = Regex.Match(endpointValue, @"^\[([a-fA-F0-9:]+)\]:(\d+)$");
            if (match.Success)
            {
                config.Endpoint = match.Groups[1].Value;
                if (int.TryParse(match.Groups[2].Value, out var port)) config.Port = port;
                return;
            }
        }

        var colonIdx = endpointValue.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < endpointValue.Length - 1)
        {
            config.Endpoint = endpointValue.Substring(0, colonIdx);
            if (int.TryParse(endpointValue.Substring(colonIdx + 1), out var port))
            {
                config.Port = port;
            }
        }
        else
        {
            config.Endpoint = endpointValue;
        }
    }
}
