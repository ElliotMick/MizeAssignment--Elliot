namespace MizeReservations.Api.Repository.Queries;

/// <summary>SQL for <c>dbo.SupplierStats</c>, the per-supplier ingest counters.</summary>
internal static class SupplierStatsQueries
{
    /// <summary>
    /// Adds the @Created/@Updated/@Unchanged/@Stale/@Failed deltas: how an accepted request ended. (The row normally
    /// exists already, created when the throttle accepted the request; the insert is a safety net.)
    /// </summary>
    public const string IncrementOutcomeCounters = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- UPDLOCK + SERIALIZABLE lock the key even while the row does not exist yet, so two requests cannot both insert it.
        UPDATE dbo.SupplierStats WITH (UPDLOCK, SERIALIZABLE)
        SET CreatedCount      = CreatedCount + @Created,
            UpdatedCount      = UpdatedCount + @Updated,
            UnchangedCount    = UnchangedCount + @Unchanged,
            StaleCount        = StaleCount + @Stale,
            FailedCount       = FailedCount + @Failed,
            LastActivityAtUtc = @NowUtc
        WHERE SupplierId = @SupplierId;

        IF @@ROWCOUNT = 0
            INSERT INTO dbo.SupplierStats
                (SupplierId, CreatedCount, UpdatedCount, UnchangedCount, StaleCount, FailedCount, LastActivityAtUtc)
            VALUES (@SupplierId, @Created, @Updated, @Unchanged, @Stale, @Failed, @NowUtc);

        COMMIT TRANSACTION;
        """;

    public const string SelectBySupplier = """
        SELECT SupplierId, AcceptedCount, ThrottledCount, CreatedCount, UpdatedCount, UnchangedCount, StaleCount, FailedCount,
               LastActivityAtUtc
        FROM dbo.SupplierStats
        WHERE SupplierId = @SupplierId;
        """;
}
