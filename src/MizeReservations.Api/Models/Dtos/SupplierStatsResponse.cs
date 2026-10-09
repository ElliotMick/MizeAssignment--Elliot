namespace MizeReservations.Api.Models.Dtos;

/// <summary>Body of <c>GET /api/reservations/stats/{supplierId}</c>.</summary>
/// <param name="Ingested">Requests that got past the throttle (the sum of <paramref name="Breakdown"/>).</param>
/// <param name="Throttled">Requests rejected with 429 by the rate limit.</param>
public sealed record SupplierStatsResponse(string SupplierId, long Ingested, long Throttled, IngestBreakdown Breakdown)
{
    public static SupplierStatsResponse FromModel(SupplierStats stats) => new(
        stats.SupplierId,
        stats.AcceptedCount,
        stats.ThrottledCount,
        new IngestBreakdown(stats.CreatedCount, stats.UpdatedCount, stats.UnchangedCount, stats.StaleCount, stats.FailedCount));
}

/// <summary>How the ingested requests ended. Failed: storing failed and the supplier got an error response.</summary>
public sealed record IngestBreakdown(long Created, long Updated, long Unchanged, long Stale, long Failed);
