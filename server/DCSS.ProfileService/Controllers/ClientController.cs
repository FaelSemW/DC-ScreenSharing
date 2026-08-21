using Microsoft.AspNetCore.Mvc;
using DCScreenSharing.Core.Profiles;
using DCSS.ProfileService.Services;

namespace DCSS.ProfileService.Controllers;

public class EnrollClientRequest
{
    public string EnrollmentTicket { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;
}

public class AcquireProfileRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public string SignatureBase64 { get; set; } = string.Empty;
}

[ApiController]
[Route("api/v1")]
public class ClientController : ControllerBase
{
    private readonly ProfileStoreService _store;
    private readonly ClientEnrollmentService _enrollmentService;

    public ClientController(ProfileStoreService store, ClientEnrollmentService enrollmentService)
    {
        _store = store;
        _enrollmentService = enrollmentService;
    }

    private bool ClientSupportsOpenVpn()
    {
        var headerCaps = Request.Headers["X-Client-Capabilities"].ToString();
        var queryCaps = Request.Query["capabilities"].ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();
        var combined = $"{headerCaps},{queryCaps},{userAgent}".ToLowerInvariant();

        return combined.Contains("openvpn-v1") ||
               combined.Contains("openvpn") ||
               combined.Contains("ovpn") ||
               combined.Contains("v1.0.10") ||
               combined.Contains("1.0.10") ||
               combined.Contains("v1.0.9") ||
               combined.Contains("1.0.9") ||
               combined.Contains("v1.0.8") ||
               combined.Contains("1.0.8") ||
               combined.Contains("v1.0.7") ||
               combined.Contains("1.0.7");
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        var activeGen = _store.GetActiveGenerationNumber();
        return Ok(new
        {
            status = "Healthy",
            service = "DCSS.ProfileService",
            activeGeneration = activeGen,
            timestampUtc = DateTime.UtcNow
        });
    }

    [HttpPost("client/enroll")]
    public IActionResult EnrollClient([FromBody] EnrollClientRequest request)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (!_enrollmentService.CheckRateLimit(clientIp, maxPerMinute: 300))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Rate limit exceeded." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.EnrollmentTicket) || string.IsNullOrWhiteSpace(request.PublicKeyPem))
        {
            return BadRequest(new { error = "Enrollment ticket and client public key are required." });
        }

        var (success, error, clientId) = _enrollmentService.EnrollClientWithTicket(request.EnrollmentTicket, request.PublicKeyPem, clientIp);
        if (!success)
        {
            return Unauthorized(new { error = $"Enrollment rejected: {error}" });
        }

        return Ok(new
        {
            success = true,
            clientId = clientId,
            message = "Client successfully enrolled and authorized."
        });
    }

    [HttpGet("client/challenge")]
    public IActionResult GetChallenge([FromQuery] string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { error = "Missing clientId parameter." });

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (!_enrollmentService.CheckRateLimit(clientIp, maxPerMinute: 300))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Rate limit exceeded." });
        }

        var nonce = _enrollmentService.GenerateChallenge(clientId);
        return Ok(new
        {
            nonce = nonce,
            expiresInSeconds = 60
        });
    }

    [HttpGet("catalog")]
    public IActionResult GetCatalog()
    {
        var catalog = _store.GetCurrentCatalog();
        if (catalog == null)
        {
            return Ok(new ServerCatalog
            {
                Schema = 1,
                Generation = 0,
                PublishedAtUtc = DateTime.UtcNow,
                Servers = new List<ServerEntry>()
            });
        }

        var supportsOvpn = ClientSupportsOpenVpn();
        if (!supportsOvpn)
        {
            // Backward compatibility for legacy clients (e.g. v1.0.6): filter to WireGuard only
            var filtered = new ServerCatalog
            {
                Schema = catalog.Schema,
                Generation = catalog.Generation,
                PublishedAtUtc = catalog.PublishedAtUtc,
                Servers = catalog.Servers.Where(s => VpnProtocol.IsWireGuard(s.Protocol)).ToList()
            };
            return Ok(filtered);
        }

        return Ok(catalog);
    }

    [HttpGet("servers")]
    public IActionResult GetServers()
    {
        var catalog = _store.GetCurrentCatalog();
        if (catalog == null)
            return NotFound(new { error = "No servers available." });

        var supportsOvpn = ClientSupportsOpenVpn();
        var enabled = catalog.Servers
            .Where(s => s.Enabled && (supportsOvpn || VpnProtocol.IsWireGuard(s.Protocol)))
            .ToList();

        return Ok(new
        {
            generation = catalog.Generation,
            publishedAtUtc = catalog.PublishedAtUtc,
            servers = enabled
        });
    }

    [HttpPost("servers/{serverId}/profile")]
    public IActionResult AcquireServerProfile(string serverId, [FromBody] AcquireProfileRequest request)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        if (!_enrollmentService.CheckRateLimit(clientIp, maxPerMinute: 300))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Rate limit exceeded. Too many profile requests." });
        }

        if (request == null)
            return BadRequest(new { error = "Missing proof-of-possession request payload." });

        var (valid, error) = _enrollmentService.VerifyProofOfPossession(
            request.ClientId,
            serverId,
            request.Nonce,
            request.Timestamp,
            request.SignatureBase64);

        if (!valid)
        {
            return Unauthorized(new { error = $"Unauthorized: {error}" });
        }

        var profile = _store.GetServerProfile(serverId);
        if (profile == null)
            return NotFound(new { error = $"Profile for server '{serverId}' not found in active generation." });

        return Ok(profile);
    }

    [HttpGet("manifest")]
    public IActionResult GetManifest()
    {
        var manifest = _store.GetCurrentManifest();
        if (manifest == null)
            return NotFound(new { error = "No manifest available." });

        return Ok(manifest);
    }
}
