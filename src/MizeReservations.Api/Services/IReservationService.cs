using MizeReservations.Api.Models;
using MizeReservations.Api.Models.Dtos;

namespace MizeReservations.Api.Services;

public interface IReservationService
{
    /// <summary>
    /// Checks the request against the business rules, applies the supplier's rate limit, then stores the reservation
    /// idempotently: a new reservation is created, an exact resend changes nothing, changed details overwrite it and an
    /// older version than the stored one is ignored.
    /// </summary>
    Task<IngestResult> IngestAsync(IngestReservationRequest request, CancellationToken cancellationToken);

    /// <summary>Ingested vs. throttled counters of a supplier (all zero if it never sent anything).</summary>
    Task<SupplierStats> GetStatsAsync(string supplierId, CancellationToken cancellationToken);
}
