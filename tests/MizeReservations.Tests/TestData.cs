using MizeReservations.Api.Models;
using MizeReservations.Api.Models.Dtos;

namespace MizeReservations.Tests;

internal static class TestData
{
    public static readonly DateTime CheckIn = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime CheckOut = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime UpdatedAt = new(2026, 7, 1, 10, 15, 0, DateTimeKind.Utc);

    /// <summary>A supplier id no other test uses, so tests sharing one database never see each other's rows.</summary>
    public static string NewSupplierId() => $"supplier-{Guid.NewGuid():N}";

    public static Reservation NewReservation(
        string supplierId = "supplier-1",
        string reservationId = "res-1",
        string roomId = "room-101",
        decimal price = 450.00m,
        DateTime? updatedAtUtc = null,
        byte[]? rowVersion = null) => new()
    {
        SupplierId = supplierId,
        ReservationId = reservationId,
        RoomId = roomId,
        CheckInUtc = CheckIn,
        CheckOutUtc = CheckOut,
        Price = price,
        UpdatedAtUtc = updatedAtUtc ?? UpdatedAt,
        RowVersion = rowVersion ?? []
    };

    /// <summary>The sample payload from the assignment, with optional changes.</summary>
    public static IngestReservationRequest NewRequest(
        string supplierId = "supplier-1",
        string reservationId = "res-1",
        string roomId = "room-101",
        decimal price = 450.00m,
        DateTime? updatedAtUtc = null,
        DateTime? checkOutUtc = null) => new()
    {
        SupplierId = supplierId,
        ReservationId = reservationId,
        RoomId = roomId,
        CheckIn = new DateTimeOffset(CheckIn),
        CheckOut = new DateTimeOffset(checkOutUtc ?? CheckOut),
        Price = price,
        UpdatedAtUtc = new DateTimeOffset(updatedAtUtc ?? UpdatedAt)
    };
}
