using Microsoft.AspNetCore.Mvc;
using MizeReservations.Api.Models.Dtos;
using MizeReservations.Api.Services;

namespace MizeReservations.Api.Controllers;

[ApiController]
[Route("api/reservations")]
public sealed class ReservationsController(IReservationService reservationService) : ControllerBase
{
    /// <summary>Ingests a reservation update from a supplier. Idempotent, and rate-limited per supplier.</summary>
    /// <response code="201">A new reservation was stored.</response>
    /// <response code="200">Updated, or an exact resend (outcome "Unchanged") for which nothing was written.</response>
    /// <response code="400">A field is missing, or the reservation breaks a business rule (outcome "Invalid").</response>
    /// <response code="409">Older than the stored version of the reservation; nothing was written.</response>
    /// <response code="429">The supplier exceeded its request limit; see the Retry-After header.</response>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(IngestReservationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(IngestReservationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Ingest([FromBody] IngestReservationRequest request, CancellationToken cancellationToken)
    {
        var result = await reservationService.IngestAsync(request, cancellationToken);
        return this.ToActionResult(result, request);
    }

    /// <summary>How many requests of a supplier were ingested vs. throttled.</summary>
    [HttpGet("stats/{supplierId}")]
    [ProducesResponseType(typeof(SupplierStatsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SupplierStatsResponse>> GetStats(string supplierId, CancellationToken cancellationToken)
    {
        var stats = await reservationService.GetStatsAsync(supplierId.Trim(), cancellationToken);
        return Ok(SupplierStatsResponse.FromModel(stats));
    }
}
