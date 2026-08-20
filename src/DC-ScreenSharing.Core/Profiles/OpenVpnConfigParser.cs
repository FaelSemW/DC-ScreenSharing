using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DCScreenSharing.Core.Profiles;

public class OpenVpnValidationResult
{
    public bool IsValid { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Protocol { get; set; } = "UDP"; // "UDP" or "TCP"
    public string PrimaryRemote { get; set; } = string.Empty;
    public int AdditionalRemotesCount { get; set; }
    public List<OpenVpnRemoteEndpoint> Remotes { get; set; } = new();
    public string AuthType { get; set; } = "None"; // "Username/Password", "Certificate", "Both", "None"
    public bool HasIPv6 { get; set; }
    public string Provider { get; set; } = "Custom";
    public List<string> UnsafeDirectives { get; set; } = new();
    public List<string> MissingExternalFiles { get; set; } = new();
    public OpenVpnProfileConfig? ParsedConfig { get; set; }
}

public static class OpenVpnConfigParser
{
    // Dangerous directives capable of arbitrary command execution or loading native libraries/plugins
    private static readonly HashSet<string> DangerousDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "script-security",
        "up",
        "down",
        "route-up",
        "route-pre-down",
        "ipchange",
        "learn-address",
        "client-connect",
        "client-disconnect",
        "auth-user-pass-verify",
        "tls-verify",
        "plugin",
        "management",
        "management-query-passwords",
        "management-hold",
        "management-user-pass",
        "pkcs11-providers",
        "tmp-dir"
    };

    // Explicit allowlist of supported safe OpenVPN client directives
    private static readonly HashSet<string> SafeDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "client",
        "dev",
        "dev-type",
        "dev-node",
        "proto",
        "proto-force",
        "remote",
        "remote-random",
        "remote-random-hostname",
        "resolv-retry",
        "nobind",
        "persist-key",
        "persist-tun",
        "remote-cert-tls",
        "remote-cert-ku",
        "remote-cert-eku",
        "cipher",
        "data-ciphers",
        "data-ciphers-fallback",
        "ncp-ciphers",
        "ncp-disable",
        "auth",
        "tls-client",
        "key-direction",
        "auth-user-pass",
        "auth-nocache",
        "auth-retry",
        "tun-mtu",
        "tun-mtu-extra",
        "mssfix",
        "fragment",
        "link-mtu",
        "verb",
        "mute",
        "connect-retry",
        "connect-retry-max",
        "connect-timeout",
        "reneg-sec",
        "reneg-bytes",
        "reneg-pkts",
        "topology",
        "route-nopull",
        "route-noexec",
        "redirect-gateway",
        "float",
        "hand-window",
        "tran-window",
        "ping",
        "ping-restart",
        "ping-timer-rem",
        "explicit-exit-notify",
        "compress",
        "comp-lzo",
        "sndbuf",
        "rcvbuf",
        "setenv",
        "ignore-unknown-option",
        "fast-io",
        "inactive",
        "pull",
        "pull-filter",
        "tls-version-min",
        "tls-version-max",
        "tls-cipher",
        "tls-ciphersuites",
        "ca",
        "cert",
        "key",
        "pkcs12",
        "tls-auth",
        "tls-crypt",
        "tls-crypt-v2",
        "crl-verify",
        "verify-x509-name",
        "dhcp-option",
        "block-outside-dns",
        "register-dns",
        "redirect-private",
        "allow-recursive-routing",
        "passtos",
        "txqueuelen"
    };

    public static OpenVpnValidationResult ParseAndValidate(
        string ovpnContent,
        Dictionary<string, string>? supportingFiles = null,
        string? declaredProvider = null)
    {
        var result = new OpenVpnValidationResult();

        if (string.IsNullOrWhiteSpace(ovpnContent))
        {
            result.IsValid = false;
            result.Error = "OpenVPN configuration content is empty.";
            return result;
        }

        var config = new OpenVpnProfileConfig();
        var lines = ovpnContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        bool insideInline = false;
        string currentInlineTag = string.Empty;
        var inlineBuffer = new StringBuilder();
        var inlineBlocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rawSafeLines = new List<string>();
        var unsafeFound = new List<string>();
        var missingFiles = new List<string>();

        var remotes = new List<OpenVpnRemoteEndpoint>();
        string globalProto = "udp";
        bool hasAuthUserPass = false;
        bool hasClientCert = false;
        bool hasClientKey = false;
        bool hasCaCert = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            if (insideInline)
            {
                var closingTag = $"</{currentInlineTag}>";
                if (trimmed.Equals(closingTag, StringComparison.OrdinalIgnoreCase))
                {
                    insideInline = false;
                    inlineBlocks[currentInlineTag] = inlineBuffer.ToString().Trim();
                    inlineBuffer.Clear();
                    currentInlineTag = string.Empty;
                }
                else
                {
                    inlineBuffer.AppendLine(rawLine);
                }
                continue;
            }

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            // Check for opening inline block <tag>
            if (trimmed.StartsWith('<') && trimmed.EndsWith('>') && !trimmed.StartsWith("</"))
            {
                var tagName = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                insideInline = true;
                currentInlineTag = tagName;
                inlineBuffer.Clear();
                continue;
            }

            // Tokenize directive line safely (handling quotes)
            var tokens = TokenizeDirective(trimmed);
            if (tokens.Count == 0) continue;

            var directive = tokens[0].ToLowerInvariant();

            // 1. Security Check: Reject dangerous directives
            if (DangerousDirectives.Contains(directive))
            {
                unsafeFound.Add(directive);
                continue;
            }

            // 2. Directive Parsing
            switch (directive)
            {
                case "client":
                    // Safe client flag
                    rawSafeLines.Add(trimmed);
                    break;

                case "proto":
                    if (tokens.Count > 1)
                    {
                        var protoVal = tokens[1].ToLowerInvariant();
                        if (protoVal.StartsWith("tcp"))
                        {
                            globalProto = "tcp";
                        }
                        else
                        {
                            globalProto = "udp";
                        }
                        config.Protocol = globalProto;
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "remote":
                    if (tokens.Count > 1)
                    {
                        var host = tokens[1];
                        int port = 1194;
                        string proto = globalProto;

                        if (tokens.Count > 2 && int.TryParse(tokens[2], out var parsedPort))
                        {
                            port = parsedPort;
                        }

                        if (tokens.Count > 3)
                        {
                            var rProto = tokens[3].ToLowerInvariant();
                            proto = rProto.StartsWith("tcp") ? "tcp" : "udp";
                        }

                        remotes.Add(new OpenVpnRemoteEndpoint
                        {
                            Host = host,
                            Port = port,
                            Proto = proto
                        });

                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "auth-user-pass":
                    hasAuthUserPass = true;
                    config.AuthUserPass = true;
                    if (tokens.Count > 1)
                    {
                        var externalAuthFile = tokens[1];
                        // Validate path safety
                        if (IsPathTraversal(externalAuthFile))
                        {
                            unsafeFound.Add($"Path traversal in auth-user-pass: {externalAuthFile}");
                        }
                    }
                    rawSafeLines.Add("auth-user-pass");
                    break;

                case "cipher":
                    if (tokens.Count > 1)
                    {
                        config.Cipher = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "data-ciphers":
                    if (tokens.Count > 1)
                    {
                        config.DataCiphers = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "data-ciphers-fallback":
                    if (tokens.Count > 1)
                    {
                        config.DataCiphersFallback = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "auth":
                    if (tokens.Count > 1)
                    {
                        config.Auth = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "key-direction":
                    if (tokens.Count > 1)
                    {
                        config.KeyDirection = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "remote-cert-tls":
                    if (tokens.Count > 1)
                    {
                        config.RemoteCertTls = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "resolv-retry":
                    if (tokens.Count > 1)
                    {
                        config.ResolvRetry = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "nobind":
                    config.Nobind = true;
                    rawSafeLines.Add(trimmed);
                    break;

                case "persist-key":
                    config.PersistKey = true;
                    rawSafeLines.Add(trimmed);
                    break;

                case "persist-tun":
                    config.PersistTun = true;
                    rawSafeLines.Add(trimmed);
                    break;

                case "tun-mtu":
                    if (tokens.Count > 1 && int.TryParse(tokens[1], out var parsedMtu))
                    {
                        config.TunMtu = parsedMtu;
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "mssfix":
                    if (tokens.Count > 1 && int.TryParse(tokens[1], out var parsedMssfix))
                    {
                        config.Mssfix = parsedMssfix;
                    }
                    rawSafeLines.Add(trimmed);
                    break;

                case "verb":
                    if (tokens.Count > 1 && int.TryParse(tokens[1], out var parsedVerb))
                    {
                        config.Verb = parsedVerb;
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "connect-retry":
                    if (tokens.Count > 1 && int.TryParse(tokens[1], out var parsedCr))
                    {
                        config.ConnectRetry = parsedCr;
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "connect-timeout":
                    if (tokens.Count > 1 && int.TryParse(tokens[1], out var parsedCt))
                    {
                        config.ConnectTimeout = parsedCt;
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "compress":
                    if (tokens.Count > 1)
                    {
                        config.Compress = tokens[1];
                        rawSafeLines.Add(trimmed);
                    }
                    break;

                case "ca":
                case "cert":
                case "key":
                case "tls-auth":
                case "tls-crypt":
                case "tls-crypt-v2":
                    if (tokens.Count > 1)
                    {
                        var filePath = tokens[1];
                        if (IsPathTraversal(filePath))
                        {
                            unsafeFound.Add($"Path traversal in {directive}: {filePath}");
                        }
                        else
                        {
                            // External reference: check if supplied in supportingFiles
                            var fileName = Path.GetFileName(filePath);
                            if (supportingFiles != null && (supportingFiles.ContainsKey(fileName) || supportingFiles.ContainsKey(filePath)))
                            {
                                var content = supportingFiles.ContainsKey(fileName) ? supportingFiles[fileName] : supportingFiles[filePath];
                                inlineBlocks[directive] = content;
                            }
                            else
                            {
                                missingFiles.Add(filePath);
                            }
                        }
                    }
                    break;

                default:
                    if (SafeDirectives.Contains(directive))
                    {
                        config.SafeDirectives[directive] = tokens.Count > 1 ? string.Join(" ", tokens.Skip(1)) : string.Empty;
                        rawSafeLines.Add(trimmed);
                    }
                    break;
            }
        }

        // Process inline certificate and key blocks
        if (inlineBlocks.TryGetValue("ca", out var ca))
        {
            config.CaCert = ca;
            hasCaCert = !string.IsNullOrWhiteSpace(ca);
        }
        if (inlineBlocks.TryGetValue("cert", out var cert))
        {
            config.ClientCert = cert;
            hasClientCert = !string.IsNullOrWhiteSpace(cert);
        }
        if (inlineBlocks.TryGetValue("key", out var key))
        {
            config.ClientKey = key;
            hasClientKey = !string.IsNullOrWhiteSpace(key);
        }
        if (inlineBlocks.TryGetValue("tls-auth", out var tlsAuth))
        {
            config.TlsAuthKey = tlsAuth;
        }
        if (inlineBlocks.TryGetValue("tls-crypt", out var tlsCrypt))
        {
            config.TlsCryptKey = tlsCrypt;
        }
        if (inlineBlocks.TryGetValue("tls-crypt-v2", out var tlsCryptV2))
        {
            config.TlsCryptV2Key = tlsCryptV2;
        }

        config.RemoteEndpoints = remotes;
        config.RawConfigSafe = string.Join("\n", rawSafeLines);

        // Security check summary
        if (unsafeFound.Count > 0)
        {
            result.IsValid = false;
            result.UnsafeDirectives = unsafeFound;
            result.Error = $"Configuration contains disallowed or dangerous directives: {string.Join(", ", unsafeFound)}";
            return result;
        }

        // Check if external files missing
        if (missingFiles.Count > 0 && !hasCaCert)
        {
            result.MissingExternalFiles = missingFiles;
            result.IsValid = false;
            result.Error = $"Configuration references external certificate/key files that were not provided: {string.Join(", ", missingFiles)}";
            return result;
        }

        // Check remotes
        if (remotes.Count == 0)
        {
            result.IsValid = false;
            result.Error = "Configuration does not contain any valid 'remote <host> <port>' entries.";
            return result;
        }

        var primary = remotes[0];
        result.PrimaryRemote = $"{primary.Host}:{primary.Port}";
        result.AdditionalRemotesCount = Math.Max(0, remotes.Count - 1);
        result.Remotes = remotes;
        result.Protocol = (primary.Proto ?? globalProto).ToUpperInvariant();

        // Check for IPv6 endpoint
        result.HasIPv6 = remotes.Any(r => IPAddress.TryParse(r.Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

        // Determine Auth Type
        if (hasAuthUserPass && (hasClientCert || hasClientKey))
        {
            result.AuthType = "Both";
        }
        else if (hasAuthUserPass)
        {
            result.AuthType = "Username/Password";
        }
        else if (hasClientCert || hasClientKey)
        {
            result.AuthType = "Certificate";
        }
        else
        {
            result.AuthType = "None";
        }

        // Provider Detection
        if (!string.IsNullOrWhiteSpace(declaredProvider))
        {
            result.Provider = declaredProvider;
        }
        else if (primary.Host.Contains("protonvpn", StringComparison.OrdinalIgnoreCase) || ovpnContent.Contains("proton", StringComparison.OrdinalIgnoreCase))
        {
            result.Provider = "Proton";
        }
        else if (primary.Host.Contains("vpnbook", StringComparison.OrdinalIgnoreCase) || ovpnContent.Contains("vpnbook", StringComparison.OrdinalIgnoreCase))
        {
            result.Provider = "VPNBook";
        }
        else
        {
            result.Provider = "Custom";
        }

        result.IsValid = true;
        result.ParsedConfig = config;
        return result;
    }

    public static bool IsPathTraversal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var norm = path.Replace('\\', '/');
        if (norm.Contains("..") || norm.StartsWith("/") || Regex.IsMatch(norm, @"^[a-zA-Z]:"))
        {
            return true;
        }
        return false;
    }

    private static List<string> TokenizeDirective(string line)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return tokens;

        var sb = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
