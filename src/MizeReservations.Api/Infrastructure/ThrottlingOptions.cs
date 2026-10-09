using System.ComponentModel.DataAnnotations;

namespace MizeReservations.Api.Infrastructure;

/// <summary>Per-supplier ingest limit: at most <see cref="PermitLimit"/> requests in any rolling window of <see cref="WindowSeconds"/>.</summary>
public sealed class ThrottlingOptions
{
    public const string SectionName = "Throttling";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100;

    [Range(1, 86_400)]
    public int WindowSeconds { get; set; } = 60;

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
