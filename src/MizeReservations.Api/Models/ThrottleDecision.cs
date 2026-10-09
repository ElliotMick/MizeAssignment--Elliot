namespace MizeReservations.Api.Models;

/// <summary>Answer of the per-supplier rate limiter for one request.</summary>
public sealed record ThrottleDecision(bool IsAllowed, TimeSpan RetryAfter)
{
    public static ThrottleDecision Allowed { get; } = new(true, TimeSpan.Zero);

    public static ThrottleDecision Rejected(TimeSpan retryAfter) => new(false, retryAfter);
}
