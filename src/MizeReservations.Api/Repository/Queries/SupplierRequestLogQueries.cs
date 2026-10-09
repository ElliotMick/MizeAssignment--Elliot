namespace MizeReservations.Api.Repository.Queries;

/// <summary>SQL for <c>dbo.SupplierRequestLog</c>, the per-supplier rolling window used for throttling.</summary>
internal static class SupplierRequestLogQueries
{
    /// <summary>
    /// One round trip: prune the window, count it, then either record the request (allowed) or reject it - and count
    /// the decision in dbo.SupplierStats (AcceptedCount or ThrottledCount) in the same transaction, so the stats always
    /// match what the throttle saw. Returns <c>IsAllowed</c> and <c>OldestInWindowUtc</c> (to compute Retry-After).
    /// </summary>
    public const string AcquireRequestSlot = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- Per-supplier mutex shared by every service instance: makes "count, then record" atomic, so concurrent
        -- requests can never squeeze more than @PermitLimit requests into the window.
        DECLARE @lockResult INT;
        EXEC @lockResult = sys.sp_getapplock
            @Resource = @LockResource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @lockResult < 0
            THROW 51000, 'Timed out waiting for the supplier throttle lock.', 1;

        -- Forget requests that have slid out of the rolling window.
        DELETE FROM dbo.SupplierRequestLog
        WHERE SupplierId = @SupplierId AND RequestedAtUtc <= @WindowStartUtc;

        DECLARE @requestsInWindow INT, @oldestInWindowUtc DATETIME2(7);
        SELECT @requestsInWindow = COUNT(*), @oldestInWindowUtc = MIN(RequestedAtUtc)
        FROM dbo.SupplierRequestLog
        WHERE SupplierId = @SupplierId;

        DECLARE @isAllowed BIT = IIF(@requestsInWindow < @PermitLimit, 1, 0);
        DECLARE @accepted INT = IIF(@isAllowed = 1, 1, 0);
        DECLARE @throttled INT = 1 - @accepted;

        IF @isAllowed = 1
            INSERT INTO dbo.SupplierRequestLog (SupplierId, RequestedAtUtc) VALUES (@SupplierId, @NowUtc);

        -- UPDLOCK + SERIALIZABLE lock the key even while the row does not exist yet, so two requests cannot both insert it.
        UPDATE dbo.SupplierStats WITH (UPDLOCK, SERIALIZABLE)
        SET AcceptedCount     = AcceptedCount + @accepted,
            ThrottledCount    = ThrottledCount + @throttled,
            LastActivityAtUtc = @NowUtc
        WHERE SupplierId = @SupplierId;

        IF @@ROWCOUNT = 0
            INSERT INTO dbo.SupplierStats (SupplierId, AcceptedCount, ThrottledCount, LastActivityAtUtc)
            VALUES (@SupplierId, @accepted, @throttled, @NowUtc);

        COMMIT TRANSACTION;

        SELECT @isAllowed AS IsAllowed, @oldestInWindowUtc AS OldestInWindowUtc;
        """;
}
