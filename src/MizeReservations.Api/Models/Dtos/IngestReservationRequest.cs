using System.ComponentModel.DataAnnotations;

namespace MizeReservations.Api.Models.Dtos;

/// <summary>Body of <c>POST /api/reservations/ingest</c>, as sent by suppliers.</summary>
/// <remarks>
/// Properties are nullable so that a missing field is reported as a 400 by [ApiController] instead of silently becoming
/// 0 or 0001-01-01. The business rules are checked by <see cref="FindRuleViolations"/>, which ReservationService calls
/// before doing anything else. Timestamps are <see cref="DateTimeOffset"/> so any offset a supplier sends is honoured
/// before everything is normalised to UTC.
/// </remarks>
public sealed record IngestReservationRequest
{
    [Required]
    public string? SupplierId { get; init; }

    [Required]
    public string? ReservationId { get; init; }

    [Required]
    public string? RoomId { get; init; }

    [Required]
    public DateTimeOffset? CheckIn { get; init; }

    [Required]
    public DateTimeOffset? CheckOut { get; init; }

    [Required]
    public decimal? Price { get; init; }

    [Required]
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>
    /// The business rules dbo.Reservations also enforces (column sizes, CK_Reservations_StayDates, CK_Reservations_Price,
    /// DECIMAL(18,2)), so a violation is reported field by field instead of surfacing as a SQL error. Every broken rule is
    /// returned, keyed by the JSON field name; an empty dictionary means the request can be stored.
    /// </summary>
    public Dictionary<string, string[]> FindRuleViolations()
    {
        var errors = new Dictionary<string, List<string>>();

        CheckId("supplierId", SupplierId);
        CheckId("reservationId", ReservationId);
        CheckId("roomId", RoomId);
        CheckPresent("checkIn", CheckIn);
        CheckPresent("checkOut", CheckOut);
        CheckPresent("updatedAtUtc", UpdatedAtUtc);
        CheckPresent("price", Price);

        if (CheckIn is { } checkIn && CheckOut is { } checkOut && checkOut <= checkIn)
        {
            Add("checkOut", "checkOut must be after checkIn.");
        }

        if (Price is { } price)
        {
            if (price is < 0 or > Reservation.MaxPrice)
            {
                Add("price", $"price must be between 0 and {Reservation.MaxPrice}.");
            }

            // Extra decimals would be rounded away by the column, and a resend would then no longer match the stored row.
            if (decimal.Round(price, Reservation.PriceDecimalPlaces) != price)
            {
                Add("price", $"price can have at most {Reservation.PriceDecimalPlaces} decimal places.");
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());

        void CheckId(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Add(field, $"{field} is required.");
            }
            else if (value.Trim().Length > Reservation.MaxIdLength)
            {
                Add(field, $"{field} can be at most {Reservation.MaxIdLength} characters long.");
            }
        }

        void CheckPresent<T>(string field, T? value) where T : struct
        {
            if (value is null)
            {
                Add(field, $"{field} is required.");
            }
        }

        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages))
            {
                errors[field] = messages = [];
            }

            messages.Add(message);
        }
    }

    /// <summary>
    /// Maps the request to the domain model: IDs trimmed, every instant converted to UTC.
    /// Only valid once <see cref="FindRuleViolations"/> found nothing.
    /// </summary>
    public Reservation ToReservation() => new()
    {
        SupplierId = SupplierId!.Trim(),
        ReservationId = ReservationId!.Trim(),
        RoomId = RoomId!.Trim(),
        CheckInUtc = CheckIn!.Value.UtcDateTime,
        CheckOutUtc = CheckOut!.Value.UtcDateTime,
        Price = Price!.Value,
        UpdatedAtUtc = UpdatedAtUtc!.Value.UtcDateTime
    };
}
