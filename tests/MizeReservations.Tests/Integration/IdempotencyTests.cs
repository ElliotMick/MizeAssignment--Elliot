using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MizeReservations.Api.Infrastructure;
using MizeReservations.Api.Models;
using MizeReservations.Api.Repository;
using MizeReservations.Api.Services;

namespace MizeReservations.Tests.Integration;

/// <summary>The idempotency rules end to end: the real service on the real database, including concurrent requests.</summary>
[Collection(LocalDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IdempotencyTests
{
    private readonly LocalDbFixture _database;
    private readonly ReservationRepository _repository;
    private readonly ReservationService _service;

    public IdempotencyTests(LocalDbFixture database)
    {
        _database = database;
        _repository = new ReservationRepository(database.ConnectionFactory);
        _service = new ReservationService(
            _repository,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)),
            Options.Create(new ThrottlingOptions()),
            Options.Create(new IngestOptions()),
            NullLogger<ReservationService>.Instance);
    }

    [Fact]
    public async Task Lifecycle_Created_Unchanged_Updated_ThenStaleForADelayedOlderVersion()
    {
        var supplierId = TestData.NewSupplierId();
        var version1 = TestData.NewRequest(supplierId);
        var version2 = TestData.NewRequest(supplierId, price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1));

        Assert.Equal(IngestOutcome.Created, (await _service.IngestAsync(version1, default)).Outcome);
        var afterCreate = await GetStoredAsync(supplierId);

        Assert.Equal(IngestOutcome.Unchanged, (await _service.IngestAsync(version1, default)).Outcome);
        Assert.Equal(afterCreate.RowVersion, (await GetStoredAsync(supplierId)).RowVersion); // nothing was written

        Assert.Equal(IngestOutcome.Updated, (await _service.IngestAsync(version2, default)).Outcome);
        var afterUpdate = await GetStoredAsync(supplierId);
        Assert.Equal(500m, afterUpdate.Price);

        var stale = await _service.IngestAsync(version1, default); // a delayed retry of the first version
        Assert.Equal(IngestOutcome.Stale, stale.Outcome);
        Assert.Equal(version2.UpdatedAtUtc!.Value.UtcDateTime, stale.StoredUpdatedAtUtc);
        var afterStale = await GetStoredAsync(supplierId);
        Assert.Equal(afterUpdate.RowVersion, afterStale.RowVersion); // nothing was written
        Assert.Equal(500m, afterStale.Price);

        var stats = await _service.GetStatsAsync(supplierId, default);
        Assert.Equal(1, stats.CreatedCount);
        Assert.Equal(1, stats.UnchangedCount);
        Assert.Equal(1, stats.UpdatedCount);
        Assert.Equal(1, stats.StaleCount);
        Assert.Equal(0, stats.FailedCount);
        Assert.Equal(4, stats.AcceptedCount); // = created + unchanged + updated + stale
        Assert.Equal(0, stats.ThrottledCount);
    }

    [Fact]
    public async Task ParallelIdenticalRequests_StoreOneRow_AndExactlyOneIsCreated()
    {
        var supplierId = TestData.NewSupplierId();
        var request = TestData.NewRequest(supplierId);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => _service.IngestAsync(request, default))));

        Assert.Equal(1, results.Count(r => r.Outcome == IngestOutcome.Created));
        Assert.Equal(19, results.Count(r => r.Outcome == IngestOutcome.Unchanged));
        Assert.Equal(1, await _database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Reservations WHERE SupplierId = @supplierId", new { supplierId }));
    }

    [Fact]
    public async Task ParallelVersions_TheNewestVersionWins()
    {
        // Each lost race means another request's write succeeded, so with N concurrent versions a request loses at most
        // N - 1 races (its first attempt can lose two: the insert and an update). N = MaxWriteAttempts therefore
        // exercises the retries without ever exhausting them.
        var supplierId = TestData.NewSupplierId();
        var versions = Enumerable.Range(1, new IngestOptions().MaxWriteAttempts)
            .Select(i => TestData.NewRequest(supplierId, price: 100m * i, updatedAtUtc: TestData.UpdatedAt.AddMinutes(i)))
            .ToList();

        var results = await Task.WhenAll(versions.Select(v => Task.Run(() => _service.IngestAsync(v, default))));

        var newest = versions[^1];
        var stored = await GetStoredAsync(supplierId);
        Assert.True(stored.HasSameDetailsAs(newest.ToReservation()));
        Assert.Equal(1, results.Count(r => r.Outcome == IngestOutcome.Created));
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { IngestOutcome.Created, IngestOutcome.Updated, IngestOutcome.Stale }));
    }

    private async Task<Reservation> GetStoredAsync(string supplierId) =>
        await _repository.GetAsync(supplierId, "res-1", default) ?? throw new InvalidOperationException("Reservation not stored.");
}
