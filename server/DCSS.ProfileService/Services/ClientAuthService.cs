using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCSS.ProfileService.Services;

public class ClientSessionPayload
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(15);
    public string ClientIp { get; set; } = string.Empty;
}

public class ClientAuthService
{
    private readonly byte[] _hmacKey = new byte[32];
    private readonly ConcurrentDictionary<string, List<DateTime>> _ipRateLimitTracker = new();
    private readonly int _maxRequestsPerMinute = 15;

    public ClientAuthService()
    {
        RandomNumberGenerator.Fill(_hmacKey);
    }

    public string IssueClientSessionToken(string clientIp)
    {
        var payload = new ClientSessionPayload
        {
            SessionId = Guid.NewGuid().ToString("N"),
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            ClientIp = clientIp
        };

        var json = JsonSerializer.Serialize(payload);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var base64Payload = Convert.ToBase64String(jsonBytes);

        using var hmac = new HMACSHA256(_hmacKey);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Payload)));

        return $"{base64Payload}.{signature}";
    }

    public bool ValidateClientSessionToken(string? token, string clientIp)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 2)
            return false;

        var base64Payload = parts[0];
        var providedSignature = parts[1];

        using var hmac = new HMACSHA256(_hmacKey);
        var computedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Payload)));

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSignature),
            Encoding.UTF8.GetBytes(computedSignature)))
        {
            return false;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(base64Payload);
            var payload = JsonSerializer.Deserialize<ClientSessionPayload>(Encoding.UTF8.GetString(jsonBytes));

            if (payload == null || payload.ExpiresAtUtc < DateTime.UtcNow)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool CheckRateLimit(string clientIp)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddMinutes(-1);

        var timestamps = _ipRateLimitTracker.GetOrAdd(clientIp, _ => new List<DateTime>());
        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < windowStart);
            if (timestamps.Count >= _maxRequestsPerMinute)
            {
                return false; // Rate limit exceeded
            }
            timestamps.Add(now);
            return true;
        }
    }
}
