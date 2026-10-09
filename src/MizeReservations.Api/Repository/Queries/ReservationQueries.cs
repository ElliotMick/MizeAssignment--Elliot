namespace MizeReservations.Api.Repository.Queries;

/// <summary>SQL for <c>dbo.Reservations</c>.</summary>
internal static class ReservationQueries
{
    public const string SelectByKey = """
        SELECT SupplierId, ReservationId, RoomId, CheckInUtc, CheckOutUtc, Price, UpdatedAtUtc,
               CreatedAtUtc, LastModifiedAtUtc, RowVersion
        FROM dbo.Reservations
        WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId;
        """;

    /// <summary>Fails with error 2627 when PK_Reservations already holds this supplier + reservation.</summary>
    public const string Insert = """
        INSERT INTO dbo.Reservations
            (SupplierId, ReservationId, RoomId, CheckInUtc, CheckOutUtc, Price, UpdatedAtUtc, CreatedAtUtc, LastModifiedAtUtc)
        VALUES
            (@SupplierId, @ReservationId, @RoomId, @CheckInUtc, @CheckOutUtc, @Price, @UpdatedAtUtc, @NowUtc, @NowUtc);
        """;

    /// <summary>Optimistic concurrency: affects no row if anybody wrote the row since it was read.</summary>
    public const string UpdateIfRowVersionMatches = """
        UPDATE dbo.Reservations
        SET RoomId            = @RoomId,
            CheckInUtc        = @CheckInUtc,
            CheckOutUtc       = @CheckOutUtc,
            Price             = @Price,
            UpdatedAtUtc      = @UpdatedAtUtc,
            LastModifiedAtUtc = @NowUtc
        WHERE SupplierId = @SupplierId
          AND ReservationId = @ReservationId
          AND RowVersion = @ExpectedRowVersion;
        """;
}
