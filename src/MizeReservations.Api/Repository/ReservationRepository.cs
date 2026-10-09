using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using MizeReservations.Api.Infrastructure;
using MizeReservations.Api.Models;
using MizeReservations.Api.Repository.Queries;

namespace MizeReservations.Api.Repository;

public sealed class ReservationRepository(ISqlConnectionFactory connectionFactory) : IReservationRepository
{
    // SQL Server errors raised when a write would duplicate a key: 2627 = PRIMARY KEY / UNIQUE constraint, 2601 = unique index.
    private const int DuplicateKeyConstraintError = 2627;
    private const int DuplicateKeyIndexError = 2601;

    static ReservationRepository()
    {
        // Dapper sends DateTime parameters as the legacy 'datetime' type (~3 ms precision) by default. The columns are
        // DATETIME2(7): without this mapping a resent payload would no longer compare equal to the stored row.
        SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);
    }

    public async Task<ThrottleDecision> TryAcquireRequestSlotAsync(
        string supplierId, DateTime nowUtc, int permitLimit, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var (isAllowed, oldestInWindowUtc) = await connection.QuerySingleAsync<(bool, DateTime?)>(new CommandDefinition(
            SupplierRequestLogQueries.AcquireRequestSlot,
            new
            {
                SupplierId = supplierId,
                LockResource = $"reservations-throttle:{supplierId}",
                NowUtc = nowUtc,
                WindowStartUtc = nowUtc - window,
                PermitLimit = permitLimit
            },
            cancellationToken: cancellationToken));

        if (isAllowed)
        {
            return ThrottleDecision.Allowed;
        }

        // The window has room again as soon as its oldest request is older than the window itself.
        var retryAfter = oldestInWindowUtc!.Value + window - nowUtc;
        return ThrottleDecision.Rejected(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero);
    }

    public async Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<Reservation>(new CommandDefinition(
            ReservationQueries.SelectByKey, new { SupplierId = supplierId, ReservationId = reservationId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> TryInsertAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                ReservationQueries.Insert,
                new
                {
                    reservation.SupplierId,
                    reservation.ReservationId,
                    reservation.RoomId,
                    reservation.CheckInUtc,
                    reservation.CheckOutUtc,
                    reservation.Price,
                    reservation.UpdatedAtUtc,
                    NowUtc = nowUtc
                },
                cancellationToken: cancellationToken));
            return true;
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyConstraintError or DuplicateKeyIndexError)
        {
            // PK_Reservations rejected the row: another request (possibly on another instance) stored this
            // reservation between our read and our insert. Expected under concurrency - the caller re-reads and re-decides.
            return false;
        }
    }

    public async Task<bool> TryUpdateAsync(
        Reservation reservation, byte[] expectedRowVersion, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            ReservationQueries.UpdateIfRowVersionMatches,
            new
            {
                reservation.SupplierId,
                reservation.ReservationId,
                reservation.RoomId,
                reservation.CheckInUtc,
                reservation.CheckOutUtc,
                reservation.Price,
                reservation.UpdatedAtUtc,
                ExpectedRowVersion = expectedRowVersion,
                NowUtc = nowUtc
            },
            cancellationToken: cancellationToken));

        return affectedRows == 1;
    }

    public async Task RecordOutcomeAsync(string supplierId, IngestOutcome outcome, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (outcome is IngestOutcome.Throttled or IngestOutcome.Invalid)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
                $"Only accepted requests are recorded here; acceptance and throttling are counted by {nameof(TryAcquireRequestSlotAsync)}.");
        }

        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            SupplierStatsQueries.IncrementOutcomeCounters,
            new
            {
                SupplierId = supplierId,
                Created = outcome == IngestOutcome.Created ? 1 : 0,
                Updated = outcome == IngestOutcome.Updated ? 1 : 0,
                Unchanged = outcome == IngestOutcome.Unchanged ? 1 : 0,
                Stale = outcome == IngestOutcome.Stale ? 1 : 0,
                Failed = outcome == IngestOutcome.Failed ? 1 : 0,
                NowUtc = nowUtc
            },
            cancellationToken: cancellationToken));
    }

    public async Task<SupplierStats?> GetStatsAsync(string supplierId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<SupplierStats>(new CommandDefinition(
            SupplierStatsQueries.SelectBySupplier, new { SupplierId = supplierId }, cancellationToken: cancellationToken));
    }
}
