namespace MizeReservations.Api.Models;

/// <summary>A row of <c>dbo.Reservations</c>, identified by (<see cref="SupplierId"/>, <see cref="ReservationId"/>). Times are UTC.</summary>
public sealed class Reservation
{
    // Limits of the dbo.Reservations columns and CHECK constraints (see Migrations/001_InitialSchema.sql).
    public const int MaxIdLength = 100;
    public const int PriceDecimalPlaces = 2;
    public const decimal MaxPrice = 9_999_999_999_999_999.99m; // largest DECIMAL(18,2)

    public required string SupplierId { get; init; }

    public required string ReservationId { get; init; }

    public required string RoomId { get; init; }

    public DateTime CheckInUtc { get; init; }

    public DateTime CheckOutUtc { get; init; }

    public decimal Price { get; init; }

    /// <summary>The supplier's version timestamp: a newer value supersedes an older one.</summary>
    public DateTime UpdatedAtUtc { get; init; }

    /// <summary>Server time of the first write (set by the repository; ignored on incoming reservations).</summary>
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Server time of the latest write (set by the repository; ignored on incoming reservations).</summary>
    public DateTime LastModifiedAtUtc { get; init; }

    /// <summary>SQL Server <c>ROWVERSION</c>; changes on every write and is used for optimistic concurrency.</summary>
    public byte[] RowVersion { get; init; } = [];

    /// <summary>
    /// True when every detail the supplier sends is identical, i.e. <paramref name="other"/> is an exact resend of this reservation.
    /// </summary>
    public bool HasSameDetailsAs(Reservation other) =>
        string.Equals(RoomId, other.RoomId, StringComparison.Ordinal)
        && CheckInUtc == other.CheckInUtc
        && CheckOutUtc == other.CheckOutUtc
        && Price == other.Price
        && UpdatedAtUtc == other.UpdatedAtUtc;
}
