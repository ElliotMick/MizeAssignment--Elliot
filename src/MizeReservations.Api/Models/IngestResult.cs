namespace MizeReservations.Api.Models;

/// <summary>Result of ingesting one reservation update.</summary>
public sealed record IngestResult(IngestOutcome Outcome)
{
    /// <summary>For <see cref="IngestOutcome.Throttled"/>: how long until the supplier's window has room again.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>For <see cref="IngestOutcome.Stale"/>: the version timestamp of the newer reservation that is stored.</summary>
    public DateTime? StoredUpdatedAtUtc { get; init; }

    /// <summary>For <see cref="IngestOutcome.Invalid"/>: the broken rules, keyed by the JSON field name.</summary>
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; init; }

    public static IngestResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(IngestOutcome.Invalid) { ValidationErrors = errors };

    public static IngestResult Throttled(TimeSpan retryAfter) => new(IngestOutcome.Throttled) { RetryAfter = retryAfter };

    public static IngestResult Stale(DateTime storedUpdatedAtUtc) => new(IngestOutcome.Stale)
    {
        // Values read back from SQL Server have DateTimeKind.Unspecified; they are stored as UTC.
        StoredUpdatedAtUtc = DateTime.SpecifyKind(storedUpdatedAtUtc, DateTimeKind.Utc)
    };
}
