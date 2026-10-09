namespace MizeReservations.Api.Models.Dtos;

/// <summary>Body of a successful ingest response (201 Created / 200 OK).</summary>
public sealed record IngestReservationResponse(string SupplierId, string ReservationId, IngestOutcome Outcome, string Message)
{
    public static IngestReservationResponse For(Reservation reservation, IngestOutcome outcome) => new(
        reservation.SupplierId,
        reservation.ReservationId,
        outcome,
        outcome switch
        {
            IngestOutcome.Created => "Reservation created.",
            IngestOutcome.Updated => "Reservation updated with the new details.",
            IngestOutcome.Unchanged => "Identical to the stored reservation; nothing was written.",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a successful ingest outcome.")
        });
}
