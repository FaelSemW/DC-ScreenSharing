using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;

namespace DCSS.ProfileService.Services;

public class EnrollmentTicketRecord
{
    public string TicketHash { get; set; } = string.Empty;
    public string Description { get; set; } = "Client Enrollment";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(30);
    public bool Used { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public string? ConsumedByClientId { get; set; }
    public bool Revoked { get; set; }
}

public class EnrolledClientRecord
{
    public string ClientId { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;
    public DateTime EnrolledAtUtc { get; set; } = DateTime.UtcNow;
    public string RegisteredIp { get; set; } = string.Empty;
    public string EnrolledViaTicketHash { get; set; } = string.Empty;
    public string? EnrolledViaAccessKeyId { get; set; }
    public string? AccessKeyName { get; set; }
    public string? AccessKeyType { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public string? AppVersion { get; set; }
}

public class ActiveChallengeRecord
{
    public string Nonce { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public bool Used { get; set; }
}

public class ClientEnrollmentService
{
    private readonly string _ticketsStoragePath;
    private readonly string _clientsStoragePath;
    private readonly AccessKeyService? _accessKeyService;
    private readonly ConcurrentDictionary<string, EnrollmentTicketRecord> _tickets = new();
    private readonly ConcurrentDictionary<string, EnrolledClientRecord> _clients = new();
    private readonly ConcurrentDictionary<string, ActiveChallengeRecord> _challenges = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _ipRateLimitTracker = new();
    private readonly object _lock = new();

    public ClientEnrollmentService(IConfiguration config, AccessKeyService? accessKeyService = null)
    {
        _accessKeyService = accessKeyService;
        var basePath = config["ProfileService:StoragePath"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
        Directory.CreateDirectory(basePath);
        _ticketsStoragePath = Path.Combine(basePath, "enrollment_tickets.json");
        _clientsStoragePath = Path.Combine(basePath, "enrolled_clients.json");

        LoadState();
    }

    public ClientEnrollmentService(string storageDirectory, AccessKeyService? accessKeyService = null)
    {
        _accessKeyService = accessKeyService;
        Directory.CreateDirectory(storageDirectory);
        _ticketsStoragePath = Path.Combine(storageDirectory, "enrollment_tickets.json");
        _clientsStoragePath = Path.Combine(storageDirectory, "enrolled_clients.json");

        LoadState();
    }

    private void LoadState()
    {
        lock (_lock)
        {
            if (File.Exists(_ticketsStoragePath))
            {
                try
                {
                    var json = File.ReadAllText(_ticketsStoragePath);
                    var list = JsonSerializer.Deserialize<List<EnrollmentTicketRecord>>(json);
                    if (list != null)
                    {
                        foreach (var t in list) _tickets[t.TicketHash] = t;
                    }
                }
                catch { }
            }

            if (File.Exists(_clientsStoragePath))
            {
                try
                {
                    var json = File.ReadAllText(_clientsStoragePath);
                    var list = JsonSerializer.Deserialize<List<EnrolledClientRecord>>(json);
                    if (list != null)
                    {
                        foreach (var c in list) _clients[c.ClientId] = c;
                    }
                }
                catch { }
            }
        }
    }

    private void SaveState()
    {
        lock (_lock)
        {
            try
            {
                var ticketsJson = JsonSerializer.Serialize(_tickets.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                var tempTicketsPath = _ticketsStoragePath + ".tmp";
                File.WriteAllText(tempTicketsPath, ticketsJson);
                File.Move(tempTicketsPath, _ticketsStoragePath, overwrite: true);

                var clientsJson = JsonSerializer.Serialize(_clients.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                var tempClientsPath = _clientsStoragePath + ".tmp";
                File.WriteAllText(tempClientsPath, clientsJson);
                File.Move(tempClientsPath, _clientsStoragePath, overwrite: true);
            }
            catch { }
        }
    }

    public static string HashTicket(string plaintextTicket)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(plaintextTicket.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public (string PlaintextTicket, EnrollmentTicketRecord Record) CreateEnrollmentTicket(int validityMinutes = 30, string description = "Client Enrollment")
    {
        lock (_lock)
        {
            var rawBytes = new byte[16];
            RandomNumberGenerator.Fill(rawBytes);
            var part1 = Convert.ToHexString(rawBytes, 0, 8);
            var part2 = Convert.ToHexString(rawBytes, 8, 8);
            var plaintext = $"DCSS-ENROLL-{part1}-{part2}";
            var hash = HashTicket(plaintext);

            var record = new EnrollmentTicketRecord
            {
                TicketHash = hash,
                Description = description,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(validityMinutes),
                Used = false,
                Revoked = false
            };

            _tickets[hash] = record;
            SaveState();
            return (plaintext, record);
        }
    }

    public (bool Success, string Error, string ClientId) EnrollClientWithTicket(string plaintextTicketOrKey, string publicKeyPem, string clientIp)
    {
        if (string.IsNullOrWhiteSpace(plaintextTicketOrKey))
            return (false, "Enrollment ticket or access key is required.", string.Empty);

        if (string.IsNullOrWhiteSpace(publicKeyPem))
            return (false, "Client public key is required.", string.Empty);

        lock (_lock)
        {
            // 1. Try modern AccessKeyService first
            if (_accessKeyService != null)
            {
                var (keySuccess, keyError, key) = _accessKeyService.ValidateAndConsumeKey(plaintextTicketOrKey);
                if (keySuccess && key != null)
                {
                    var newClientId = Guid.NewGuid().ToString("N");
                    var clientRecord = new EnrolledClientRecord
                    {
                        ClientId = newClientId,
                        PublicKeyPem = publicKeyPem,
                        EnrolledAtUtc = DateTime.UtcNow,
                        RegisteredIp = clientIp,
                        EnrolledViaTicketHash = key.CodeHash,
                        EnrolledViaAccessKeyId = key.Id,
                        AccessKeyName = key.Name,
                        AccessKeyType = key.Type,
                        IsActive = true,
                        LastSeenAtUtc = DateTime.UtcNow
                    };

                    _clients[newClientId] = clientRecord;
                    SaveState();

                    return (true, string.Empty, newClientId);
                }
                else if (key != null) // Key was found in AccessKey store but failed validation (expired/revoked/capacity)
                {
                    return (false, keyError, string.Empty);
                }
            }

            // 2. Fallback to legacy ticket store
            var hash = HashTicket(plaintextTicketOrKey);
            if (!_tickets.TryGetValue(hash, out var ticket))
            {
                return (false, "Invalid enrollment ticket or access key.", string.Empty);
            }

            if (ticket.Revoked)
            {
                return (false, "Enrollment ticket has been revoked by an administrator.", string.Empty);
            }

            if (ticket.Used)
            {
                return (false, "Enrollment ticket has already been used.", string.Empty);
            }

            if (ticket.ExpiresAtUtc < DateTime.UtcNow)
            {
                return (false, "Enrollment ticket has expired.", string.Empty);
            }

            // Consume legacy ticket
            var legacyClientId = Guid.NewGuid().ToString("N");
            ticket.Used = true;
            ticket.ConsumedAtUtc = DateTime.UtcNow;
            ticket.ConsumedByClientId = legacyClientId;

            var legacyClientRecord = new EnrolledClientRecord
            {
                ClientId = legacyClientId,
                PublicKeyPem = publicKeyPem,
                EnrolledAtUtc = DateTime.UtcNow,
                RegisteredIp = clientIp,
                EnrolledViaTicketHash = hash,
                AccessKeyName = ticket.Description,
                AccessKeyType = "LEGACY_TICKET",
                IsActive = true,
                LastSeenAtUtc = DateTime.UtcNow
            };

            _clients[legacyClientId] = legacyClientRecord;
            SaveState();

            return (true, string.Empty, legacyClientId);
        }
    }

    public string GenerateChallenge(string clientId)
    {
        var nonceBytes = new byte[32];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Convert.ToBase64String(nonceBytes);

        var challenge = new ActiveChallengeRecord
        {
            Nonce = nonce,
            ClientId = clientId,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(60),
            Used = false
        };

        _challenges[nonce] = challenge;
        return nonce;
    }

    public (bool Success, string Error) VerifyProofOfPossession(string clientId, string serverId, string nonce, long timestamp, string signatureBase64)
    {
        if (!_clients.TryGetValue(clientId, out var client))
        {
            return (false, "Client identity is not enrolled.");
        }

        if (!client.IsActive)
        {
            return (false, "Client identity has been revoked.");
        }

        client.LastSeenAtUtc = DateTime.UtcNow;

        if (!_challenges.TryGetValue(nonce, out var challenge))
        {
            return (false, "Invalid or unknown challenge nonce.");
        }

        if (challenge.Used)
        {
            return (false, "Challenge nonce has already been consumed (replay detected).");
        }

        if (challenge.ClientId != clientId || challenge.ExpiresAtUtc < DateTime.UtcNow)
        {
            return (false, "Challenge nonce has expired or does not belong to client.");
        }

        challenge.Used = true;

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        if (Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes) > 2)
        {
            return (false, "Request timestamp is out of tolerance.");
        }

        var expectedPayload = $"{clientId}:{serverId}:{nonce}:{timestamp}";
        var verified = ProfileCrypto.VerifySignature(expectedPayload, signatureBase64, client.PublicKeyPem);

        if (!verified)
        {
            return (false, "Cryptographic proof-of-possession signature verification failed.");
        }

        return (true, string.Empty);
    }

    public bool RevokeClient(string clientId)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.IsActive = false;
                client.RevokedAtUtc = DateTime.UtcNow;
                SaveState();
                return true;
            }
            return false;
        }
    }

    public bool RestoreClient(string clientId)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.IsActive = true;
                client.RevokedAtUtc = null;
                SaveState();
                return true;
            }
            return false;
        }
    }

    public int RevokeClientsByAccessKey(string accessKeyId, string? codeHash)
    {
        lock (_lock)
        {
            int revokedCount = 0;
            var now = DateTime.UtcNow;
            foreach (var client in _clients.Values)
            {
                if ((!string.IsNullOrEmpty(client.EnrolledViaAccessKeyId) && client.EnrolledViaAccessKeyId == accessKeyId) ||
                    (!string.IsNullOrEmpty(codeHash) && client.EnrolledViaTicketHash == codeHash))
                {
                    if (client.IsActive)
                    {
                        client.IsActive = false;
                        client.RevokedAtUtc = now;
                        revokedCount++;
                    }
                }
            }
            if (revokedCount > 0)
            {
                SaveState();
            }
            return revokedCount;
        }
    }

    public IReadOnlyList<EnrolledClientRecord> GetClientsByAccessKey(string accessKeyId, string? codeHash)
    {
        return _clients.Values.Where(c => 
            (!string.IsNullOrEmpty(c.EnrolledViaAccessKeyId) && c.EnrolledViaAccessKeyId == accessKeyId) ||
            (!string.IsNullOrEmpty(codeHash) && c.EnrolledViaTicketHash == codeHash))
            .OrderByDescending(c => c.EnrolledAtUtc)
            .ToList();
    }

    public bool RevokeTicket(string ticketHash)
    {
        lock (_lock)
        {
            if (_tickets.TryGetValue(ticketHash, out var ticket))
            {
                ticket.Revoked = true;
                SaveState();
                return true;
            }
            return false;
        }
    }

    public IReadOnlyList<EnrollmentTicketRecord> GetTickets()
    {
        return _tickets.Values.OrderByDescending(t => t.CreatedAtUtc).ToList();
    }

    public IReadOnlyList<EnrolledClientRecord> GetClients(string? status = null, string? search = null)
    {
        var query = _clients.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(status))
        {
            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                query = query.Where(c => c.IsActive);
            else if (string.Equals(status, "revoked", StringComparison.OrdinalIgnoreCase))
                query = query.Where(c => !c.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(c =>
                c.ClientId.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                (c.AccessKeyName != null && c.AccessKeyName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (c.RegisteredIp != null && c.RegisteredIp.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (c.AppVersion != null && c.AppVersion.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }

        return query.OrderByDescending(c => c.EnrolledAtUtc).ToList();
    }

    public EnrolledClientRecord? GetClientById(string clientId)
    {
        _clients.TryGetValue(clientId, out var client);
        return client;
    }

    public bool CheckRateLimit(string clientIp, int maxPerMinute = 20)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddMinutes(-1);

        var timestamps = _ipRateLimitTracker.GetOrAdd(clientIp, _ => new List<DateTime>());
        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < windowStart);
            if (timestamps.Count >= maxPerMinute)
            {
                return false;
            }
            timestamps.Add(now);
            return true;
        }
    }
}
