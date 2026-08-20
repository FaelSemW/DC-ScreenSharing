using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using DCScreenSharing.Core.Profiles;
using DCSS.ProfileService.Services;

namespace DCSS.ProfileService.Controllers;

public class RollbackRequest
{
    public int TargetGeneration { get; set; }
}

public class CreateTicketRequest
{
    public int ValidityMinutes { get; set; } = 30;
    public string Description { get; set; } = "Client Enrollment";
}

public class AdminLoginRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

public class CreateAccessKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = AccessKeyType.SingleUse;
    public string Expiration { get; set; } = "30d"; // 1h, 24h, 7d, 30d, 90d, 1y, custom, never
    public DateTime? CustomExpiresAtUtc { get; set; }
    public int? MaxUses { get; set; }
}

public class RevokeKeyRequest
{
    public bool RevokeClients { get; set; } = false;
}

public class AddServerRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Country { get; set; } = "US";
    public string CountryCode { get; set; } = "US";
    public string Region { get; set; } = string.Empty;
    public string? City { get; set; }
    public string Provider { get; set; } = "Custom";
    public string ConfContent { get; set; } = string.Empty;
    public bool PublishImmediately { get; set; } = false;
}

public class UpdateServerRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool PublishImmediately { get; set; } = false;
}

public class ValidateOpenVpnRequest
{
    public string OvpnContent { get; set; } = string.Empty;
    public string Provider { get; set; } = "Custom";
    public Dictionary<string, string>? SupportingFiles { get; set; }
}

public class AddOpenVpnServerRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? City { get; set; }
    public string Provider { get; set; } = "Custom";
    public string OvpnContent { get; set; } = string.Empty;
    public string? CredentialSetId { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public Dictionary<string, string>? SupportingFiles { get; set; }
    public bool PublishImmediately { get; set; } = false;
}

public class BulkImportOpenVpnRequest
{
    public List<AddOpenVpnServerRequest> Servers { get; set; } = new();
}

public class UpdateOpenVpnServerRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? City { get; set; }
    public string Provider { get; set; } = "Custom";
    public string? CredentialSetId { get; set; }
    public bool Enabled { get; set; } = true;
}

public class CreateCredentialSetRequest
{
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "Custom";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class UpdateCredentialSetRequest
{
    public string? Name { get; set; }
    public string? Provider { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

[ApiController]
[Route("api/v1/admin")]
public class AdminController : ControllerBase
{
    private readonly ProfileStoreService _store;
    private readonly ClientEnrollmentService _enrollmentService;
    private readonly AccessKeyService _accessKeyService;
    private readonly CredentialSetService _credentialSetService;
    private readonly AuditLogService _auditLog;
    private readonly IConfiguration _config;
    private static readonly ConcurrentDictionary<string, List<DateTime>> _failedLoginRateLimiter = new();

    public AdminController(
        ProfileStoreService store,
        ClientEnrollmentService enrollmentService,
        AccessKeyService accessKeyService,
        CredentialSetService credentialSetService,
        AuditLogService auditLog,
        IConfiguration config)
    {
        _store = store;
        _enrollmentService = enrollmentService;
        _accessKeyService = accessKeyService;
        _credentialSetService = credentialSetService;
        _auditLog = auditLog;
        _config = config;
    }

    private string GetAdminApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
        var configKey = _config["ProfileService:AdminApiKey"];
        return !string.IsNullOrEmpty(envKey) ? envKey : (!string.IsNullOrEmpty(configKey) ? configKey : "dev-admin-secret-key-replace-in-prod");
    }

    private string GetClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

    // ----------------------------------------------------
    // AUTHENTICATION
    // ----------------------------------------------------

    [HttpPost("auth/login")]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request)
    {
        var ip = GetClientIp();
        var now = DateTime.UtcNow;

        // Rate limiting failed attempts: max 20 failed per minute
        var attempts = _failedLoginRateLimiter.GetOrAdd(ip, _ => new List<DateTime>());
        lock (attempts)
        {
            attempts.RemoveAll(t => t < now.AddMinutes(-1));
            if (attempts.Count >= 20)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed login attempts. Please wait a minute." });
            }
        }

        var adminApiKey = GetAdminApiKey();

        if (string.IsNullOrWhiteSpace(request?.ApiKey))
        {
            lock (attempts) { attempts.Add(now); }
            _auditLog.Record("AdminLoginFailed", "Anonymous", ip, metadata: new() { ["reason"] = "Empty key" });
            return Unauthorized(new { error = "Invalid administrator credentials." });
        }

        var isMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.ApiKey.Trim()),
            Encoding.UTF8.GetBytes(adminApiKey));

        if (!isMatch)
        {
            lock (attempts) { attempts.Add(now); }
            _auditLog.Record("AdminLoginFailed", "Anonymous", ip, metadata: new() { ["reason"] = "Bad key" });
            return Unauthorized(new { error = "Invalid administrator credentials." });
        }

        // Clear failed attempts on successful login
        lock (attempts) { attempts.Clear(); }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "admin"),
            new(ClaimTypes.Role, "Administrator"),
            new("AuthenticatedAt", DateTime.UtcNow.ToString("O"))
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var authProps = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);
        _auditLog.Record("AdminLoginSucceeded", "admin", ip);

        return Ok(new
        {
            success = true,
            message = "Authentication successful.",
            role = "admin"
        });
    }

    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout()
    {
        var ip = GetClientIp();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        _auditLog.Record("AdminLogout", "admin", ip);
        return Ok(new { success = true, message = "Signed out successfully." });
    }

    [HttpGet("auth/session")]
    public IActionResult GetSession()
    {
        var isAuth = User?.Identity?.IsAuthenticated == true;
        return Ok(new
        {
            authenticated = isAuth,
            role = isAuth ? "admin" : null,
            serverTimeUtc = DateTime.UtcNow
        });
    }

    // ----------------------------------------------------
    // DASHBOARD
    // ----------------------------------------------------

    [HttpGet("dashboard")]
    public IActionResult GetDashboard()
    {
        var allClients = _enrollmentService.GetClients();
        var allKeys = _accessKeyService.GetAllKeys();
        var allServers = _store.GetServers();
        var activeGen = _store.GetActiveGenerationNumber();
        var recentAudit = _auditLog.GetEvents(limit: 5);

        return Ok(new
        {
            activeClientsCount = allClients.Count(c => c.IsActive),
            revokedClientsCount = allClients.Count(c => !c.IsActive),
            activeKeysCount = allKeys.Count(k => k.Status == AccessKeyStatus.Active),
            groupKeysCount = allKeys.Count(k => k.Type == AccessKeyType.Group),
            availableServersCount = allServers.Count(s => s.Enabled),
            wireGuardServersCount = allServers.Count(s => s.Enabled && VpnProtocol.IsWireGuard(s.Protocol)),
            openVpnServersCount = allServers.Count(s => s.Enabled && VpnProtocol.IsOpenVpn(s.Protocol)),
            currentGeneration = activeGen,
            backendHealth = "Healthy",
            recentActivations = allClients.Take(5).Select(c => new
            {
                c.ClientId,
                c.AccessKeyName,
                c.AccessKeyType,
                c.EnrolledAtUtc,
                c.IsActive
            }),
            recentAudit
        });
    }

    // ----------------------------------------------------
    // ACCESS KEYS
    // ----------------------------------------------------

    [HttpGet("access-keys")]
    public IActionResult GetAccessKeys()
    {
        var keys = _accessKeyService.GetAllKeys();
        return Ok(keys);
    }

    [HttpPost("access-keys")]
    public IActionResult CreateAccessKey([FromBody] CreateAccessKeyRequest request)
    {
        if (request == null)
            return BadRequest(new { error = "Invalid request payload." });

        DateTime? expiresAt = null;
        var exp = (request.Expiration ?? "30d").ToLowerInvariant().Trim();
        var now = DateTime.UtcNow;

        switch (exp)
        {
            case "1h":
                expiresAt = now.AddHours(1);
                break;
            case "24h":
                expiresAt = now.AddHours(24);
                break;
            case "7d":
                expiresAt = now.AddDays(7);
                break;
            case "30d":
                expiresAt = now.AddDays(30);
                break;
            case "90d":
                expiresAt = now.AddDays(90);
                break;
            case "1y":
                expiresAt = now.AddYears(1);
                break;
            case "custom":
                expiresAt = request.CustomExpiresAtUtc;
                break;
            case "never":
                expiresAt = null;
                break;
        }

        var isGroup = string.Equals(request.Type, AccessKeyType.Group, StringComparison.OrdinalIgnoreCase);
        int? maxUses = isGroup ? request.MaxUses : 1;

        var (plaintextCode, record) = _accessKeyService.CreateAccessKey(
            name: request.Name,
            type: request.Type,
            expiresAtUtc: expiresAt,
            maxUses: maxUses,
            createdBy: "Admin");

        _auditLog.Record("AccessKeyCreated", "admin", GetClientIp(), targetId: record.Id, metadata: new Dictionary<string, string>
        {
            ["name"] = record.Name,
            ["type"] = record.Type
        });

        return Ok(new
        {
            success = true,
            record = record,
            key = record,
            accessKey = plaintextCode,
            plaintextCode = plaintextCode
        });
    }

    [HttpPost("access-keys/{id}/disable")]
    public IActionResult DisableAccessKey(string id)
    {
        var success = _accessKeyService.DisableKey(id);
        if (!success) return NotFound(new { error = "Key not found." });

        _auditLog.Record("AccessKeyDisabled", "admin", GetClientIp(), targetId: id);
        return Ok(new { success = true, message = "Access key disabled." });
    }

    [HttpPost("access-keys/{id}/enable")]
    public IActionResult EnableAccessKey(string id)
    {
        var success = _accessKeyService.EnableKey(id);
        if (!success) return NotFound(new { error = "Key not found." });

        _auditLog.Record("AccessKeyEnabled", "admin", GetClientIp(), targetId: id);
        return Ok(new { success = true, message = "Access key enabled." });
    }

    [HttpPost("access-keys/{id}/revoke")]
    public IActionResult RevokeAccessKey(string id, [FromBody] RevokeKeyRequest? request)
    {
        var key = _accessKeyService.GetKeyById(id);
        if (key == null) return NotFound(new { error = "Key not found." });

        var success = _accessKeyService.RevokeKey(id);
        if (!success) return NotFound(new { error = "Key not found." });

        int revokedClientsCount = 0;
        if (request?.RevokeClients == true)
        {
            revokedClientsCount = _enrollmentService.RevokeClientsByAccessKey(id, key.CodeHash);
        }

        _auditLog.Record("AccessKeyRevoked", "admin", GetClientIp(), targetId: id, metadata: new()
        {
            ["name"] = key.Name,
            ["revokedClientsCount"] = revokedClientsCount.ToString()
        });

        return Ok(new
        {
            success = true,
            message = $"Access key revoked. {revokedClientsCount} associated client(s) revoked.",
            revokedClientsCount
        });
    }

    [HttpGet("access-keys/{id}/usage")]
    public IActionResult GetKeyUsage(string id)
    {
        var key = _accessKeyService.GetKeyById(id);
        if (key == null) return NotFound(new { error = "Key not found." });

        var clients = _enrollmentService.GetClientsByAccessKey(id, key.CodeHash);
        return Ok(new
        {
            key,
            clients
        });
    }

    // ----------------------------------------------------
    // ENROLLMENT TICKETS (LEGACY)
    // ----------------------------------------------------

    [HttpPost("tickets")]
    public IActionResult CreateTicket([FromBody] CreateTicketRequest? request)
    {
        var validity = request?.ValidityMinutes > 0 ? request.ValidityMinutes : 30;
        var (ticket, record) = _enrollmentService.CreateEnrollmentTicket(validity, request?.Description ?? "Client Enrollment");

        _auditLog.Record("TicketCreated", "admin", GetClientIp(), targetId: record.TicketHash);

        return Ok(new
        {
            ticket = ticket,
            ticketHash = record.TicketHash,
            expiresAtUtc = record.ExpiresAtUtc,
            description = record.Description
        });
    }

    [HttpPost("tickets/{ticketHash}/revoke")]
    public IActionResult RevokeTicket(string ticketHash)
    {
        var success = _enrollmentService.RevokeTicket(ticketHash);
        if (!success) return NotFound(new { error = "Ticket not found." });

        _auditLog.Record("TicketRevoked", "admin", GetClientIp(), targetId: ticketHash);
        return Ok(new { success = true, message = "Ticket revoked." });
    }

    [HttpGet("tickets")]
    public IActionResult GetTickets()
    {
        return Ok(_enrollmentService.GetTickets());
    }

    // ----------------------------------------------------
    // CLIENT MANAGEMENT
    // ----------------------------------------------------

    [HttpGet("clients")]
    public IActionResult GetClients([FromQuery] string? status = null, [FromQuery] string? search = null)
    {
        var clients = _enrollmentService.GetClients(status, search);
        return Ok(clients);
    }

    [HttpGet("clients/{clientId}")]
    public IActionResult GetClientById(string clientId)
    {
        var client = _enrollmentService.GetClientById(clientId);
        if (client == null) return NotFound(new { error = "Client not found." });
        return Ok(client);
    }

    [HttpPost("clients/{clientId}/revoke")]
    public IActionResult RevokeClient(string clientId)
    {
        var success = _enrollmentService.RevokeClient(clientId);
        if (!success) return NotFound(new { error = "Client not found." });

        _auditLog.Record("ClientRevoked", "admin", GetClientIp(), targetId: clientId);
        return Ok(new { success = true, message = "Client access revoked." });
    }

    [HttpPost("clients/{clientId}/restore")]
    public IActionResult RestoreClient(string clientId)
    {
        var success = _enrollmentService.RestoreClient(clientId);
        if (!success) return NotFound(new { error = "Client not found." });

        _auditLog.Record("ClientRestored", "admin", GetClientIp(), targetId: clientId);
        return Ok(new { success = true, message = "Client access restored." });
    }

    // ----------------------------------------------------
    // SERVER MANAGEMENT (WIREGUARD + OPENVPN)
    // ----------------------------------------------------

    [HttpGet("servers")]
    public IActionResult GetServers([FromQuery] string? protocol = null)
    {
        var servers = _store.GetServers(protocol);
        return Ok(servers);
    }

    [HttpGet("servers/publication-status")]
    public IActionResult GetPublicationStatus()
    {
        var status = _store.GetPublicationStatus();
        return Ok(status);
    }

    [HttpPost("servers")]
    public IActionResult AddWireGuardServer([FromBody] AddServerRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ConfContent))
            return BadRequest(new { error = "WireGuard configuration content (.conf) is required." });

        var (success, error, entry, profile) = ProfileStoreService.ParseWireGuardConfig(
            request.ConfContent,
            request.DisplayName,
            request.Country,
            request.Region,
            request.Provider);

        if (!success || entry == null || profile == null)
        {
            return BadRequest(new { error = $"Invalid WireGuard profile: {error}" });
        }

        if (!string.IsNullOrEmpty(request.CountryCode))
        {
            entry.CountryCode = request.CountryCode.Trim();
        }
        if (!string.IsNullOrEmpty(request.City))
        {
            entry.City = request.City.Trim();
        }

        _store.AddWireGuardServer(entry, profile, request.Country, request.Provider);
        _auditLog.Record("ServerAdded", "admin", GetClientIp(), targetId: entry.Id, metadata: new()
        {
            ["name"] = entry.Name,
            ["protocol"] = "WIREGUARD",
            ["country"] = request.Country,
            ["endpoint"] = profile.Wireguard.Endpoint
        });

        if (request.PublishImmediately)
        {
            var (pubSuccess, pubError, newGen) = _store.CreateAndPublishNewGeneration(publishedBy: "Admin Web Console (Save & Publish)");
            if (pubSuccess)
            {
                return Ok(new
                {
                    success = true,
                    published = true,
                    generation = newGen,
                    server = entry,
                    message = $"WireGuard server '{entry.Name}' added and published in Generation #{newGen}."
                });
            }
            return Ok(new
            {
                success = true,
                published = false,
                publishError = pubError,
                server = entry,
                message = $"WireGuard server '{entry.Name}' added to registry, but publish failed: {pubError}"
            });
        }

        return Ok(new
        {
            success = true,
            published = false,
            server = entry,
            message = $"WireGuard server '{entry.Name}' added to registry. Remember to publish changes to clients."
        });
    }

    [HttpPost("openvpn/validate")]
    public IActionResult ValidateOpenVpn([FromBody] ValidateOpenVpnRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.OvpnContent))
        {
            return BadRequest(new { error = "OpenVPN configuration content (.ovpn) is required." });
        }

        var validation = OpenVpnConfigParser.ParseAndValidate(
            request.OvpnContent,
            request.SupportingFiles,
            request.Provider);

        return Ok(new
        {
            isValid = validation.IsValid,
            error = validation.Error,
            protocol = validation.Protocol,
            primaryRemote = validation.PrimaryRemote,
            additionalRemotesCount = validation.AdditionalRemotesCount,
            remotes = validation.Remotes,
            authType = validation.AuthType,
            hasIPv6 = validation.HasIPv6,
            provider = validation.Provider,
            unsafeDirectives = validation.UnsafeDirectives,
            missingExternalFiles = validation.MissingExternalFiles
        });
    }

    [HttpPost("servers/openvpn")]
    public IActionResult AddOpenVpnServer([FromBody] AddOpenVpnServerRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.OvpnContent))
            return BadRequest(new { error = "OpenVPN configuration content (.ovpn) is required." });

        var (success, error, entry, profile) = ProfileStoreService.ParseOpenVpnConfig(
            request.OvpnContent,
            request.DisplayName,
            request.Country,
            request.CountryCode,
            request.Region,
            request.City,
            request.Provider,
            request.CredentialSetId,
            request.Username,
            request.Password,
            request.SupportingFiles);

        if (!success || entry == null || profile == null)
        {
            _auditLog.Record("OpenVpnProfileRejected", "admin", GetClientIp(), metadata: new()
            {
                ["reason"] = error,
                ["country"] = request.Country
            });
            return BadRequest(new { error = $"Invalid OpenVPN profile: {error}" });
        }

        _store.AddOpenVpnServer(entry, profile, request.Country, request.CountryCode, request.City, request.Provider, request.CredentialSetId);

        _auditLog.Record("OpenVpnServerImported", "admin", GetClientIp(), targetId: entry.Id, metadata: new()
        {
            ["name"] = entry.Name,
            ["protocol"] = "OPENVPN",
            ["provider"] = entry.Provider,
            ["country"] = entry.Country,
            ["primaryRemote"] = profile.Openvpn?.RemoteEndpoints.FirstOrDefault()?.Host ?? "N/A"
        });

        if (request.PublishImmediately)
        {
            var (pubSuccess, pubError, newGen) = _store.CreateAndPublishNewGeneration(publishedBy: "Admin Web Console (Save & Publish)");
            if (pubSuccess)
            {
                return Ok(new
                {
                    success = true,
                    published = true,
                    generation = newGen,
                    server = entry,
                    message = $"OpenVPN server '{entry.Name}' added and published in Generation #{newGen}."
                });
            }
            return Ok(new
            {
                success = true,
                published = false,
                publishError = pubError,
                server = entry,
                message = $"OpenVPN server '{entry.Name}' added to registry, but publish failed: {pubError}"
            });
        }

        return Ok(new
        {
            success = true,
            published = false,
            server = entry,
            message = $"OpenVPN server '{entry.Name}' added to registry. Remember to publish changes to clients."
        });
    }

    [HttpPost("servers/openvpn/bulk")]
    public IActionResult BulkImportOpenVpn([FromBody] BulkImportOpenVpnRequest request)
    {
        if (request?.Servers == null || request.Servers.Count == 0)
        {
            return BadRequest(new { error = "No OpenVPN profiles provided for bulk import." });
        }

        var imported = new List<ServerEntry>();
        var errors = new List<string>();

        foreach (var s in request.Servers)
        {
            if (string.IsNullOrWhiteSpace(s.OvpnContent)) continue;

            var (success, error, entry, profile) = ProfileStoreService.ParseOpenVpnConfig(
                s.OvpnContent,
                s.DisplayName,
                s.Country,
                s.CountryCode,
                s.Region,
                s.City,
                s.Provider,
                s.CredentialSetId,
                s.Username,
                s.Password,
                s.SupportingFiles);

            if (success && entry != null && profile != null)
            {
                _store.AddOpenVpnServer(entry, profile, s.Country, s.CountryCode, s.City, s.Provider, s.CredentialSetId);
                imported.Add(entry);

                _auditLog.Record("OpenVpnServerImported", "admin", GetClientIp(), targetId: entry.Id, metadata: new()
                {
                    ["name"] = entry.Name,
                    ["provider"] = entry.Provider,
                    ["batch"] = "BulkImport"
                });
            }
            else
            {
                errors.Add($"{s.DisplayName ?? s.Country}: {error}");
            }
        }

        return Ok(new
        {
            success = true,
            importedCount = imported.Count,
            imported,
            errors,
            message = $"Successfully imported {imported.Count} OpenVPN server(s)."
        });
    }

    [HttpPut("servers/{serverId}")]
    public IActionResult UpdateServer(string serverId, [FromBody] UpdateServerRequest request)
    {
        if (request == null) return BadRequest(new { error = "Invalid request." });

        var success = _store.UpdateServer(serverId, request.DisplayName, request.Region, request.Enabled);
        if (!success) return NotFound(new { error = "Server not found." });

        _auditLog.Record("ServerUpdated", "admin", GetClientIp(), targetId: serverId, metadata: new()
        {
            ["name"] = request.DisplayName,
            ["region"] = request.Region,
            ["enabled"] = request.Enabled.ToString()
        });

        return Ok(new { success = true, message = "Server updated." });
    }

    [HttpPut("servers/openvpn/{serverId}")]
    public IActionResult UpdateOpenVpnServer(string serverId, [FromBody] UpdateOpenVpnServerRequest request)
    {
        if (request == null) return BadRequest(new { error = "Invalid request." });

        var success = _store.UpdateOpenVpnServer(
            serverId,
            request.DisplayName,
            request.Region,
            request.City,
            request.Provider,
            request.CredentialSetId,
            request.Enabled);

        if (!success) return NotFound(new { error = "OpenVPN server not found." });

        _auditLog.Record("OpenVpnServerUpdated", "admin", GetClientIp(), targetId: serverId, metadata: new()
        {
            ["name"] = request.DisplayName,
            ["provider"] = request.Provider,
            ["credentialSetId"] = request.CredentialSetId ?? "none"
        });

        return Ok(new { success = true, message = "OpenVPN server updated." });
    }

    [HttpPost("servers/{serverId}/enable")]
    public IActionResult EnableServer(string serverId)
    {
        var success = _store.SetServerEnabled(serverId, true);
        if (!success) return NotFound(new { error = "Server not found." });

        _auditLog.Record("ServerEnabled", "admin", GetClientIp(), targetId: serverId);
        return Ok(new { success = true, message = "Server enabled." });
    }

    [HttpPost("servers/{serverId}/disable")]
    public IActionResult DisableServer(string serverId)
    {
        var success = _store.SetServerEnabled(serverId, false);
        if (!success) return NotFound(new { error = "Server not found." });

        _auditLog.Record("OpenVpnServerDisabled", "admin", GetClientIp(), targetId: serverId);
        return Ok(new { success = true, message = "Server disabled." });
    }

    [HttpDelete("servers/{serverId}")]
    public IActionResult DeleteServer(string serverId)
    {
        var success = _store.DeleteServer(serverId);
        if (!success) return NotFound(new { error = "Server not found." });

        _auditLog.Record("ServerRemoved", "admin", GetClientIp(), targetId: serverId);
        return Ok(new { success = true, message = "Server removed from registry." });
    }

    // ----------------------------------------------------
    // OPENVPN CREDENTIAL SETS
    // ----------------------------------------------------

    [HttpGet("openvpn/credential-sets")]
    public IActionResult GetCredentialSets()
    {
        var sets = _credentialSetService.GetAllDtos();
        return Ok(sets);
    }

    [HttpPost("openvpn/credential-sets")]
    public IActionResult CreateCredentialSet([FromBody] CreateCredentialSetRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Credential set name is required." });
        }

        var created = _credentialSetService.Create(
            request.Name,
            request.Provider,
            request.Username,
            request.Password);

        _auditLog.Record("OpenVpnCredentialSetCreated", "admin", GetClientIp(), targetId: created.Id, metadata: new()
        {
            ["name"] = created.Name,
            ["provider"] = created.Provider,
            ["username"] = created.Username
        });

        return Ok(new
        {
            success = true,
            credentialSet = created,
            message = $"Credential set '{created.Name}' created."
        });
    }

    [HttpPut("openvpn/credential-sets/{id}")]
    public IActionResult UpdateCredentialSet(string id, [FromBody] UpdateCredentialSetRequest request)
    {
        if (request == null) return BadRequest(new { error = "Invalid payload." });

        var success = _credentialSetService.Update(
            id,
            request.Name,
            request.Provider,
            request.Username,
            request.Password);

        if (!success) return NotFound(new { error = "Credential set not found." });

        _auditLog.Record("OpenVpnCredentialSetUpdated", "admin", GetClientIp(), targetId: id, metadata: new()
        {
            ["name"] = request.Name ?? "Updated",
            ["passwordRotated"] = (!string.IsNullOrEmpty(request.Password)).ToString()
        });

        return Ok(new { success = true, message = "Credential set updated." });
    }

    [HttpDelete("openvpn/credential-sets/{id}")]
    public IActionResult DeleteCredentialSet(string id)
    {
        var success = _credentialSetService.Delete(id);
        if (!success) return NotFound(new { error = "Credential set not found." });

        _auditLog.Record("OpenVpnCredentialSetDeleted", "admin", GetClientIp(), targetId: id);
        return Ok(new { success = true, message = "Credential set deleted." });
    }

    // ----------------------------------------------------
    // GENERATIONS
    // ----------------------------------------------------

    [HttpGet("generations")]
    public IActionResult GetGenerations()
    {
        var history = _store.GetGenerationHistory();
        return Ok(history);
    }

    [HttpPost("generations")]
    public IActionResult CreateAndPublishGeneration()
    {
        var (success, error, newGen) = _store.CreateAndPublishNewGeneration(publishedBy: "Admin Web Console");
        if (!success)
        {
            return BadRequest(new { error = $"Failed to create generation: {error}" });
        }

        _auditLog.Record("GenerationPublished", "admin", GetClientIp(), targetId: newGen.ToString(), metadata: new()
        {
            ["generation"] = newGen.ToString()
        });

        return Ok(new
        {
            success = true,
            generation = newGen,
            message = $"Generation {newGen} compiled, signed, and published successfully."
        });
    }

    [HttpPost("generations/{genNumber}/publish")]
    public IActionResult RollbackToGeneration(int genNumber)
    {
        var success = _store.RollbackToGeneration(genNumber);
        if (!success)
        {
            return BadRequest(new { error = $"Rollback failed. Generation {genNumber} does not exist in storage." });
        }

        _auditLog.Record("GenerationPublished", "admin", GetClientIp(), targetId: genNumber.ToString(), metadata: new()
        {
            ["action"] = "Rollback/Activate"
        });

        return Ok(new
        {
            success = true,
            activeGeneration = genNumber,
            message = $"Active catalog generation switched to {genNumber}."
        });
    }

    // ----------------------------------------------------
    // AUDIT LOG & SYSTEM
    // ----------------------------------------------------

    [HttpGet("audit")]
    public IActionResult GetAuditLog([FromQuery] int limit = 100)
    {
        var events = _auditLog.GetEvents(limit);
        return Ok(events);
    }

    [HttpGet("system/info")]
    public IActionResult GetSystemInfo()
    {
        return Ok(new
        {
            osVersion = Environment.OSVersion.ToString(),
            runtime = Environment.Version.ToString(),
            processorCount = Environment.ProcessorCount,
            machineName = Environment.MachineName,
            is64Bit = Environment.Is64BitProcess,
            uptimeSeconds = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
            memoryBytes = GC.GetTotalMemory(false)
        });
    }
}
