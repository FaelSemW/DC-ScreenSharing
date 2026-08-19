using System.Text.RegularExpressions;

namespace DCScreenSharing.Shared.Logging;

public static class Sanitizer
{
    private static readonly Regex PrivateKeyFieldRegex = new(
        @"(PrivateKey|private_key|privateKey|peer_public_key|PeerPublicKey)\s*[:=]\s*[""']?([A-Za-z0-9+/]{42,44}={0,2})[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Base64KeyRegex = new(
        @"\b[A-Za-z0-9+/]{43}=\b",
        RegexOptions.Compiled);

    private static readonly Regex AuthBearerRegex = new(
        @"(Authorization\s*[:=]\s*Bearer\s+)[A-Za-z0-9\-_.]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DpapiBlobRegex = new(
        @"(dpapi|encryptedData)\s*[:=]\s*[""']?([A-Za-z0-9+/]{50,})[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sanitized = PrivateKeyFieldRegex.Replace(input, "$1: [REDACTED_KEY]");
        sanitized = AuthBearerRegex.Replace(sanitized, "$1[REDACTED_TOKEN]");
        sanitized = DpapiBlobRegex.Replace(sanitized, "$1: [REDACTED_DPAPI_BLOB]");

        // Scrub any remaining 44-char base64 WireGuard private keys
        sanitized = Base64KeyRegex.Replace(sanitized, "[REDACTED_KEY]");

        return sanitized;
    }
}
