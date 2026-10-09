namespace MizeReservations.Api.Models;

/// <summary>What happened to an ingest request.</summary>
public enum IngestOutcome
{
    /// <summary>A new reservation was stored.</summary>
    Created,

    /// <summary>The stored reservation was overwritten with changed details.</summary>
    Updated,

    /// <summary>An exact resend of the stored reservation; nothing was written.</summary>
    Unchanged,

    /// <summary>Older than the stored version of the reservation; nothing was written.</summary>
    Stale,

    /// <summary>Rejected by the per-supplier rate limit; nothing was written.</summary>
    Throttled,

    /// <summary>Breaks a business rule of the reservations table (dates, price, ID length); nothing was written.</summary>
    Invalid,

    /// <summary>
    /// Passed the throttle, but storing it failed (an exception, or the client disconnected). Only recorded in the
    /// supplier's stats; the request itself ends with an error response.
    /// </summary>
    Failed
}
