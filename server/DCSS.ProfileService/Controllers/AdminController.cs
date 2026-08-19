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

[ApiController]
[Route("api/v1/admin")]
public class AdminController : ControllerBase
{
    private readonly ProfileStoreService _store;
    private readonly ClientEnrollmentService _enrollmentService;

    public AdminController(ProfileStoreService store, ClientEnrollmentService enrollmentService)
    {
        _store = store;
        _enrollmentService = enrollmentService;
    }

    [HttpGet("generations")]
    public IActionResult GetGenerations()
    {
        var history = _store.GetGenerationHistory();
        return Ok(history);
    }

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

    [HttpGet("clients")]
    public IActionResult GetClients()
    {
        var clients = _enrollmentService.GetClients();
        return Ok(clients);
    }

    [HttpPost("clients/{clientId}/revoke")]
    public IActionResult RevokeClient(string clientId)
    {
        var success = _enrollmentService.RevokeClient(clientId);
        if (!success) return NotFound(new { error = "Client not found." });

        return Ok(new { success = true, message = "Client access revoked." });
    }
}
