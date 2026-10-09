using MizeReservations.Api.Models;
using MizeReservations.Api.Repository;

namespace MizeReservations.Tests.Integration;

[Collection(LocalDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReservationRepositoryTests(LocalDbFixture database)
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly ReservationRepository _repository = new(database.ConnectionFactory);

    [Fact]
    public async Task TryInsert_OfAnExistingKey_ReturnsFalseInsteadOfThrowing_AndKeepsTheStoredRow()
    {
        var supplierId = TestData.NewSupplierId();
        Assert.True(await _repository.TryInsertAsync(TestData.NewReservation(supplierId), Now, default));

        // PK_Reservations rejects the second insert; the repository turns that SqlException into "false".
        Assert.False(await _repository.TryInsertAsync(TestData.NewReservation(supplierId, price: 999m), Now, default));

        var stored = await _repository.GetAsync(supplierId, "res-1", default);
        Assert.Equal(450.00m, stored!.Price);
        Assert.Equal(1, await CountReservationsAsync(supplierId));
    }

    [Fact]
    public async Task SameReservationId_FromTwoSuppliers_AreTwoReservations()
    {
        var supplierA = TestData.NewSupplierId();
        var supplierB = TestData.NewSupplierId();

        Assert.True(await _repository.TryInsertAsync(TestData.NewReservation(supplierA, "R-1"), Now, default));
        Assert.True(await _repository.TryInsertAsync(TestData.NewReservation(supplierB, "R-1"), Now, default));
    }

    [Fact]
    public async Task ReservationIds_AreCaseSensitive()
    {
        var supplierId = TestData.NewSupplierId();

        Assert.True(await _repository.TryInsertAsync(TestData.NewReservation(supplierId, "R-1", price: 100m), Now, default));
        Assert.True(await _repository.TryInsertAsync(TestData.NewReservation(supplierId, "r-1", price: 200m), Now, default));

        Assert.Equal(100m, (await _repository.GetAsync(supplierId, "R-1", default))!.Price);
        Assert.Equal(200m, (await _repository.GetAsync(supplierId, "r-1", default))!.Price);
    }

    [Fact]
    public async Task StoredReservation_ReadsBackExactly_SoAResendStillMatches()
    {
        // Seven fractional-second digits and a cents price: any precision lost on the way into SQL Server
        // would make every resend of this payload look like a change.
        var supplierId = TestData.NewSupplierId();
        var reservation = TestData.NewReservation(supplierId, price: 1234.56m, updatedAtUtc: TestData.UpdatedAt.AddTicks(1_234_567));
        await _repository.TryInsertAsync(reservation, Now, default);

        var stored = await _repository.GetAsync(supplierId, "res-1", default);

        Assert.NotNull(stored);
        Assert.True(stored.HasSameDetailsAs(reservation));
        Assert.Equal(Now, stored.CreatedAtUtc);
        Assert.Equal(Now, stored.LastModifiedAtUtc);
        Assert.Equal(8, stored.RowVersion.Length);
    }

    [Fact]
    public async Task TryUpdate_AppliesOnlyWhileTheRowVersionIsUnchanged()
    {
        var supplierId = TestData.NewSupplierId();
        await _repository.TryInsertAsync(TestData.NewReservation(supplierId), Now, default);
        var original = (await _repository.GetAsync(supplierId, "res-1", default))!;

        var first = TestData.NewReservation(supplierId, price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1));
        Assert.True(await _repository.TryUpdateAsync(first, original.RowVersion, Now.AddMinutes(1), default));

        // A second writer that decided on the original row must not overwrite the first writer's change.
        var second = TestData.NewReservation(supplierId, price: 600m, updatedAtUtc: TestData.UpdatedAt.AddHours(2));
        Assert.False(await _repository.TryUpdateAsync(second, original.RowVersion, Now.AddMinutes(2), default));

        var stored = (await _repository.GetAsync(supplierId, "res-1", default))!;
        Assert.Equal(500m, stored.Price);
        Assert.Equal(Now, stored.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(1), stored.LastModifiedAtUtc);
        Assert.NotEqual(original.RowVersion, stored.RowVersion);
    }

    [Fact]
    public async Task RecordOutcome_AccumulatesPerSupplierCounters()
    {
        var supplierId = TestData.NewSupplierId();

        await _repository.RecordOutcomeAsync(supplierId, IngestOutcome.Created, Now, default);
        await _repository.RecordOutcomeAsync(supplierId, IngestOutcome.Unchanged, Now, default);
        await _repository.RecordOutcomeAsync(supplierId, IngestOutcome.Unchanged, Now, default);
        await _repository.RecordOutcomeAsync(supplierId, IngestOutcome.Stale, Now, default);
        await _repository.RecordOutcomeAsync(supplierId, IngestOutcome.Failed, Now, default);

        var stats = (await _repository.GetStatsAsync(supplierId, default))!;
        Assert.Equal(1, stats.CreatedCount);
        Assert.Equal(0, stats.UpdatedCount);
        Assert.Equal(2, stats.UnchangedCount);
        Assert.Equal(1, stats.StaleCount);
        Assert.Equal(1, stats.FailedCount);
        Assert.Equal(0, stats.ThrottledCount);
    }

    private Task<int> CountReservationsAsync(string supplierId) =>
        database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Reservations WHERE SupplierId = @supplierId", new { supplierId });
}
