namespace MizeReservations.Api.Models;

/// <summary>A row of <c>dbo.SupplierStats</c>: how each ingest request of a supplier was resolved.</summary>
public sealed class SupplierStats
{
    public required string SupplierId { get; init; }

    /// <summary>Requests that got past the throttle, counted atomically with the throttle decision.</summary>
    public long AcceptedCount { get; init; }

    /// <summary>Requests rejected by the throttle, counted atomically with the throttle decision.</summary>
    public long ThrottledCount { get; init; }

    // How the accepted requests ended: AcceptedCount = Created + Updated + Unchanged + Stale + Failed.
    public long CreatedCount { get; init; }

    public long UpdatedCount { get; init; }

    public long UnchangedCount { get; init; }

    public long StaleCount { get; init; }

    public long FailedCount { get; init; }

    public DateTime LastActivityAtUtc { get; init; }

    /// <summary>Stats of a supplier that has not sent anything yet.</summary>
    public static SupplierStats Empty(string supplierId) => new() { SupplierId = supplierId };
}
