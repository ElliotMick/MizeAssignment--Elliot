using MizeReservations.Api.Models;

namespace MizeReservations.Api.Repository;

/// <summary>
/// SQL Server data access for reservations, the per-supplier throttle window and supplier stats.
/// Every method is safe to call concurrently from any number of service instances.
/// </summary>
public interface IReservationRepository
{
    /// <summary>
    /// Atomically checks the supplier's rolling window and, if it has room, records this request in it. When the window
    /// is full the request is rejected. Either way the decision is counted in the supplier's stats (accepted or
    /// throttled) in the same transaction. Only allowed requests occupy the window.
    /// </summary>
    Task<ThrottleDecision> TryAcquireRequestSlotAsync(
        string supplierId, DateTime nowUtc, int permitLimit, TimeSpan window, CancellationToken cancellationToken);

    Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new reservation. Returns <c>false</c> - instead of throwing - when the primary key rejects it because
    /// this supplier + reservation is already stored (earlier, or a moment ago by a concurrent request).
    /// </summary>
    Task<bool> TryInsertAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites the stored reservation, but only if its row version still equals <paramref name="expectedRowVersion"/>.
    /// Returns <c>false</c> when a concurrent request changed the row after it was read.
    /// </summary>
    Task<bool> TryUpdateAsync(
        Reservation reservation, byte[] expectedRowVersion, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Counts how an accepted request ended (created, updated, unchanged, stale or failed). Acceptance and throttling
    /// themselves are counted by <see cref="TryAcquireRequestSlotAsync"/>.
    /// </summary>
    Task RecordOutcomeAsync(string supplierId, IngestOutcome outcome, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Returns the supplier's counters, or <c>null</c> if it never sent a request.</summary>
    Task<SupplierStats?> GetStatsAsync(string supplierId, CancellationToken cancellationToken);
}
