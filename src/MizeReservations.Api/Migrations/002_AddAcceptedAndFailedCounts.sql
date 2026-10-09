/*
    002_AddAcceptedAndFailedCounts.sql

    AcceptedCount: requests allowed past the throttle, counted in the same transaction that records them in the
                   rolling window - so "ingested" always matches what the throttle saw, like ThrottledCount does.
    FailedCount:   accepted requests whose storing failed (e.g. 503 after repeated concurrent modifications, 500, or
                   the client disconnecting), so AcceptedCount = Created + Updated + Unchanged + Stale + Failed.

    Re-runnable like 001. GO separates the batches: SQL Server compiles a whole batch up front, so the backfill cannot
    reference AcceptedCount in the batch that adds it.
*/

IF COL_LENGTH(N'dbo.SupplierStats', N'AcceptedCount') IS NULL
    ALTER TABLE dbo.SupplierStats
        ADD AcceptedCount BIGINT NOT NULL CONSTRAINT DF_SupplierStats_AcceptedCount DEFAULT (0);

IF COL_LENGTH(N'dbo.SupplierStats', N'FailedCount') IS NULL
    ALTER TABLE dbo.SupplierStats
        ADD FailedCount BIGINT NOT NULL CONSTRAINT DF_SupplierStats_FailedCount DEFAULT (0);
GO

-- Rows written before this migration: every recorded outcome was an accepted request.
UPDATE dbo.SupplierStats
SET AcceptedCount = CreatedCount + UpdatedCount + UnchangedCount + StaleCount
WHERE AcceptedCount = 0;
