using System.Data;
using Microsoft.Extensions.Options;
using MizeReservations.Api.Infrastructure;
using MizeReservations.Api.Models;
using MizeReservations.Api.Models.Dtos;
using MizeReservations.Api.Repository;

namespace MizeReservations.Api.Services;

public sealed class ReservationService(
    IReservationRepository repository,
    TimeProvider timeProvider,
    IOptions<ThrottlingOptions> throttlingOptions,
    IOptions<IngestOptions> ingestOptions,
    ILogger<ReservationService> logger) : IReservationService
{
    public async Task<IngestResult> IngestAsync(IngestReservationRequest request, CancellationToken cancellationToken)
    {
        // Sanity check first: an invalid reservation costs no database work and is not counted against the supplier.
        var ruleViolations = request.FindRuleViolations();
        if (ruleViolations.Count > 0)
        {
            logger.LogInformation("Rejected reservation {SupplierId}/{ReservationId}: it breaks {Rules}.",
                request.SupplierId, request.ReservationId, string.Join(", ", ruleViolations.Keys));
            return IngestResult.Invalid(ruleViolations);
        }

        var incoming = request.ToReservation();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var limits = throttlingOptions.Value;

        var slot = await repository.TryAcquireRequestSlotAsync(
            incoming.SupplierId, nowUtc, limits.PermitLimit, limits.Window, cancellationToken);

        if (!slot.IsAllowed)
        {
            logger.LogInformation(
                "Throttled supplier {SupplierId}: its window has room again in {RetryAfter}.", incoming.SupplierId, slot.RetryAfter);
            return IngestResult.Throttled(slot.RetryAfter);
        }

        // The request is now counted as accepted (atomically with the throttle decision), so however it ends it must be
        // counted in the breakdown too - otherwise the stats would no longer add up.
        IngestResult result;
        try
        {
            result = await StoreAsync(incoming, nowUtc, cancellationToken);
        }
        catch
        {
            await RecordOutcomeAsync(incoming.SupplierId, IngestOutcome.Failed, nowUtc);
            throw;
        }

        await RecordOutcomeAsync(incoming.SupplierId, result.Outcome, nowUtc);
        return result;
    }

    public async Task<SupplierStats> GetStatsAsync(string supplierId, CancellationToken cancellationToken) =>
        await repository.GetStatsAsync(supplierId, cancellationToken) ?? SupplierStats.Empty(supplierId);

    /// <summary>
    /// Counts how an accepted request ended. Not cancellable: once the reservation write is done, a client that hangs up
    /// must not stop the counter from catching up. Best effort: the reservation itself is already safe, so a failure here
    /// is logged rather than turned into an error response.
    /// </summary>
    private async Task RecordOutcomeAsync(string supplierId, IngestOutcome outcome, DateTime nowUtc)
    {
        try
        {
            await repository.RecordOutcomeAsync(supplierId, outcome, nowUtc, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record outcome {Outcome} in the stats of supplier {SupplierId}.", outcome, supplierId);
        }
    }

    /// <summary>
    /// Insert first, so a new reservation is stored in a single round trip. If the composite primary key rejects the
    /// insert, the reservation already exists (stored earlier, or a moment ago by a concurrent request on any instance):
    /// read it and decide - unchanged, stale, or update. An update only applies if the row version is still the one we
    /// read; losing such a race is not an error - we read again and re-decide, which normally ends as Unchanged or Stale.
    /// <see cref="IngestOptions.MaxWriteAttempts"/> bounds the attempts: the first one starts with the insert, later ones
    /// with a fresh read, and each ends with at most one update.
    /// </summary>
    private async Task<IngestResult> StoreAsync(Reservation incoming, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var maxWriteAttempts = ingestOptions.Value.MaxWriteAttempts;
        var mayBeNew = true;

        for (var attempt = 1; attempt <= maxWriteAttempts; attempt++)
        {
            if (mayBeNew && await repository.TryInsertAsync(incoming, nowUtc, cancellationToken))
            {
                return new IngestResult(IngestOutcome.Created);
            }

            var existing = await repository.GetAsync(incoming.SupplierId, incoming.ReservationId, cancellationToken);

            // Only possible if the row was deleted after our insert was rejected: try inserting again.
            mayBeNew = existing is null;
            if (existing is null)
            {
                continue;
            }

            if (existing.HasSameDetailsAs(incoming))
            {
                return new IngestResult(IngestOutcome.Unchanged);
            }

            if (incoming.UpdatedAtUtc < existing.UpdatedAtUtc)
            {
                logger.LogInformation(
                    "Ignored stale update of {SupplierId}/{ReservationId}: received version {IncomingVersion:O}, stored version {StoredVersion:O}.",
                    incoming.SupplierId, incoming.ReservationId, incoming.UpdatedAtUtc, existing.UpdatedAtUtc);
                return IngestResult.Stale(existing.UpdatedAtUtc);
            }

            if (await repository.TryUpdateAsync(incoming, existing.RowVersion, nowUtc, cancellationToken))
            {
                return new IngestResult(IngestOutcome.Updated);
            }

            logger.LogDebug("Reservation {SupplierId}/{ReservationId} was modified concurrently; re-evaluating.",
                incoming.SupplierId, incoming.ReservationId);
        }

        logger.LogWarning("Gave up on reservation {SupplierId}/{ReservationId} after {Attempts} write attempts lost to concurrent requests.",
            incoming.SupplierId, incoming.ReservationId, maxWriteAttempts);
        throw new DBConcurrencyException(
            $"Reservation '{incoming.ReservationId}' of supplier '{incoming.SupplierId}' kept changing concurrently; " +
            $"gave up after {maxWriteAttempts} write attempts.");
    }
}
