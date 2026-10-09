using System.ComponentModel.DataAnnotations;

namespace MizeReservations.Api.Infrastructure;

/// <summary>Settings of the ingest flow (appsettings section "Ingest").</summary>
public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>
    /// How many times one ingest may decide and write (insert first, then read-and-update) when concurrent requests for
    /// the same reservation keep winning the race. Once exhausted the request fails with 503 and the supplier retries.
    /// </summary>
    [Range(1, 20)]
    public int MaxWriteAttempts { get; set; } = 5;
}
