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
    public string Region { get; set; } = string.Empty;
    public string ConfContent { get; set; } = string.Empty;
}

public class UpdateServerRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

[ApiController]
[Route("api/v1/admin")]
public class AdminController : ControllerBase
{
    private readonly ProfileStoreService _store;
    private readonly ClientEnrollmentService _enrollmentService;
    private readonly AccessKeyService _accessKeyService;
    private readonly AuditLogService _auditLog;
    private readonly IConfiguration _config;
    private static readonly ConcurrentDictionary<string, List<DateTime>> _failedLoginRateLimiter = new();

    public AdminController(
        ProfileStoreService store,
        ClientEnrollmentService enrollmentService,
        AccessKeyService accessKeyService,
        AuditLogService auditLog,
        IConfiguration config)
    {
        _store = store;
        _enrollmentService = enrollmentService;
        _accessKeyService = accessKeyService;
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
            case "never":
                expiresAt = null;
                break;
            case "custom":
                expiresAt = request.CustomExpiresAtUtc;
                break;
            default:
                expiresAt = now.AddDays(30);
                break;
        }

        var isSingleUse = string.Equals(request.Type, AccessKeyType.SingleUse, StringComparison.OrdinalIgnoreCase);
        int? maxUses = isSingleUse ? 1 : (request.MaxUses is > 0 ? request.MaxUses : null);

        var (plaintext, record) = _accessKeyService.CreateAccessKey(
            request.Name,
            request.Type,
            expiresAt,
            maxUses,
            createdBy: "Admin");

        _auditLog.Record("AccessKeyCreated", "admin", GetClientIp(), targetId: record.Id, metadata: new()
        {
            ["name"] = record.Name,
            ["type"] = record.Type,
            ["maxUses"] = record.MaxUses?.ToString() ?? "Unlimited",
            ["expiresAt"] = record.ExpiresAtUtc?.ToString("O") ?? "Never"
        });

        return Ok(new
        {
            success = true,
            accessKey = plaintext,
            record = record,
            message = "Access key created successfully. Store the code securely; it cannot be viewed again."
        });
    }

    [HttpPost("access-keys/{id}/disable")]
    public IActionResult DisableAccessKey(string id)
    {
        var success = _accessKeyService.DisableKey(id);
        if (!success) return NotFound(new { error = "Access key not found." });

        _auditLog.Record("AccessKeyDisabled", "admin", GetClientIp(), targetId: id);
        return Ok(new { success = true, message = "Access key disabled." });
    }

    [HttpPost("access-keys/{id}/enable")]
    public IActionResult EnableAccessKey(string id)
    {
        var success = _accessKeyService.EnableKey(id);
        if (!success) return NotFound(new { error = "Access key not found." });

        _auditLog.Record("AccessKeyEnabled", "admin", GetClientIp(), targetId: id);
        return Ok(new { success = true, message = "Access key enabled." });
    }

    [HttpPost("access-keys/{id}/revoke")]
    public IActionResult RevokeAccessKey(string id, [FromBody] RevokeKeyRequest? request)
    {
        var key = _accessKeyService.GetKeyById(id);
        if (key == null) return NotFound(new { error = "Access key not found." });

        _accessKeyService.RevokeKey(id);
        int revokedClients = 0;

        if (request?.RevokeClients == true)
        {
            revokedClients = _enrollmentService.RevokeClientsByAccessKey(id, key.CodeHash);
        }

        _auditLog.Record("AccessKeyRevoked", "admin", GetClientIp(), targetId: id, metadata: new()
        {
            ["revokedClientsCount"] = revokedClients.ToString()
        });

        return Ok(new
        {
            success = true,
            message = $"Access key revoked. {revokedClients} associated client(s) revoked.",
            revokedClientsCount = revokedClients
        });
    }

    [HttpGet("access-keys/{id}/usage")]
    public IActionResult GetAccessKeyUsage(string id)
    {
        var key = _accessKeyService.GetKeyById(id);
        if (key == null) return NotFound(new { error = "Access key not found." });

        var clients = _enrollmentService.GetClientsByAccessKey(id, key.CodeHash);
        return Ok(new
        {
            key,
            clients
        });
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
    // SERVER MANAGEMENT
    // ----------------------------------------------------

    [HttpGet("servers")]
    public IActionResult GetServers()
    {
        var servers = _store.GetServers();
        return Ok(servers);
    }

    [HttpPost("servers")]
    public IActionResult AddServer([FromBody] AddServerRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ConfContent))
            return BadRequest(new { error = "WireGuard configuration content (.conf) is required." });

        var (success, error, entry, profile) = ProfileStoreService.ParseWireGuardConfig(
            request.ConfContent,
            request.DisplayName,
            request.Country,
            request.Region);

        if (!success || entry == null || profile == null)
        {
            return BadRequest(new { error = $"Invalid WireGuard profile: {error}" });
        }

        _store.AddServer(entry, profile, request.Country);
        _auditLog.Record("ServerAdded", "admin", GetClientIp(), targetId: entry.Id, metadata: new()
        {
            ["name"] = entry.Name,
            ["country"] = request.Country,
            ["endpoint"] = profile.Wireguard.Endpoint
        });

        return Ok(new
        {
            success = true,
            server = entry,
            message = $"Server '{entry.Name}' added to registry. Remember to create and publish a new generation to apply changes to clients."
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

        _auditLog.Record("ServerDisabled", "admin", GetClientIp(), targetId: serverId);
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
        var logs = _auditLog.GetEvents(Math.Clamp(limit, 10, 500));
        return Ok(logs);
    }

    [HttpGet("system")]
    public IActionResult GetSystemInfo()
    {
        var activeGen = _store.GetActiveGenerationNumber();
        var allServers = _store.GetServers();
        var allClients = _enrollmentService.GetClients();
        var allKeys = _accessKeyService.GetAllKeys();

        return Ok(new
        {
            health = "Healthy",
            environment = "Production",
            framework = ".NET 8 (ASP.NET Core)",
            storage = "Persistent Railway Volume (/app/storage)",
            activeGeneration = activeGen,
            totalServersCount = allServers.Count,
            enabledServersCount = allServers.Count(s => s.Enabled),
            totalClientsCount = allClients.Count,
            activeClientsCount = allClients.Count(c => c.IsActive),
            totalAccessKeysCount = allKeys.Count,
            activeAccessKeysCount = allKeys.Count(k => k.Status == AccessKeyStatus.Active),
            serverTimeUtc = DateTime.UtcNow
        });
    }

    // ----------------------------------------------------
    // LEGACY MAINTAINER COMPATIBILITY
    // ----------------------------------------------------

    [HttpPost("publish")]
    public IActionResult PublishGeneration([FromBody] SignedManifest manifest)
    {
        if (manifest == null)
            return BadRequest(new { error = "Manifest cannot be null." });

        var published = _store.PublishGeneration(manifest, "Maintainer API");
        if (!published)
        {
            return BadRequest(new { error = "Failed to publish generation. Ensure generation number is strictly greater than current active." });
        }

        _auditLog.Record("GenerationPublished", "Maintainer", GetClientIp(), targetId: manifest.Generation.ToString());

        return Ok(new
        {
            success = true,
            generation = manifest.Generation,
            message = $"Generation {manifest.Generation} is now active."
        });
    }

    [HttpPost("rollback")]
    public IActionResult Rollback([FromBody] RollbackRequest request)
    {
        if (request == null || request.TargetGeneration <= 0)
            return BadRequest(new { error = "Invalid target generation number." });

        var success = _store.RollbackToGeneration(request.TargetGeneration);
        if (!success)
        {
            return BadRequest(new { error = $"Rollback failed. Generation {request.TargetGeneration} does not exist." });
        }

        _auditLog.Record("GenerationPublished", "Maintainer", GetClientIp(), targetId: request.TargetGeneration.ToString(), metadata: new() { ["action"] = "Rollback" });

        return Ok(new
        {
            success = true,
            activeGeneration = request.TargetGeneration,
            message = $"Successfully rolled back active generation to {request.TargetGeneration}."
        });
    }

    [HttpPost("tickets")]
    public IActionResult CreateTicket([FromBody] CreateTicketRequest request)
    {
        var validity = request?.ValidityMinutes > 0 ? request.ValidityMinutes : 30;
        var description = request?.Description ?? "Client Enrollment";

        var (plaintextTicket, record) = _enrollmentService.CreateEnrollmentTicket(validity, description);

        return Ok(new
        {
            ticket = plaintextTicket,
            ticketHash = record.TicketHash,
            expiresAtUtc = record.ExpiresAtUtc,
            validityMinutes = validity,
            message = "Single-use enrollment ticket created successfully."
        });
    }

    [HttpGet("tickets")]
    public IActionResult GetTickets()
    {
        var tickets = _enrollmentService.GetTickets();
        return Ok(tickets);
    }

    [HttpPost("tickets/{ticketHash}/revoke")]
    public IActionResult RevokeTicket(string ticketHash)
    {
        var success = _enrollmentService.RevokeTicket(ticketHash);
        if (!success) return NotFound(new { error = "Ticket not found." });

        return Ok(new { success = true, message = "Ticket revoked." });
    }
}
