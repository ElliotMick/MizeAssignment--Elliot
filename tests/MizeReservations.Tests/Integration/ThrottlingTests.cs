using MizeReservations.Api.Models;
using MizeReservations.Api.Repository;

namespace MizeReservations.Tests.Integration;

/// <summary>The rolling-window rate limit (100 requests per 60 s per supplier), driven with explicit timestamps.</summary>
[Collection(LocalDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ThrottlingTests(LocalDbFixture database)
{
    private const int Limit = 100;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly ReservationRepository _repository = new(database.ConnectionFactory);

    [Fact]
    public async Task Allows100RequestsInAWindow_AndRejectsThe101st()
    {
        var supplierId = TestData.NewSupplierId();

        await SendAllowedAsync(supplierId, T0, Limit);

        Assert.False((await RequestAt(supplierId, T0.AddSeconds(1))).IsAllowed);
    }

    [Fact]
    public async Task Window_IsRolling_SlotsFreeUpAsOldRequestsAgeOut()
    {
        var supplierId = TestData.NewSupplierId();
        await SendAllowedAsync(supplierId, T0, 50);
        await SendAllowedAsync(supplierId, T0.AddSeconds(30), 50);

        Assert.False((await RequestAt(supplierId, T0.AddSeconds(59))).IsAllowed); // all 100 still inside the window

        await SendAllowedAsync(supplierId, T0.AddSeconds(60), 50);                // the 50 sent at T0 have aged out
        Assert.False((await RequestAt(supplierId, T0.AddSeconds(60))).IsAllowed);
        Assert.False((await RequestAt(supplierId, T0.AddSeconds(89))).IsAllowed);

        Assert.True((await RequestAt(supplierId, T0.AddSeconds(90))).IsAllowed);  // ...and now those sent at T0 + 30 s
    }

    [Fact]
    public async Task RetryAfter_IsTheTimeUntilTheOldestRequestLeavesTheWindow()
    {
        var supplierId = TestData.NewSupplierId();
        await SendAllowedAsync(supplierId, T0, 1);
        await SendAllowedAsync(supplierId, T0.AddSeconds(10), Limit - 1);

        var rejected = await RequestAt(supplierId, T0.AddSeconds(25));

        Assert.False(rejected.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(35), rejected.RetryAfter);
    }

    [Fact]
    public async Task RejectedRequests_DoNotExtendTheBlock()
    {
        var supplierId = TestData.NewSupplierId();
        await SendAllowedAsync(supplierId, T0, Limit);

        for (var second = 1; second < 60; second++)
        {
            Assert.False((await RequestAt(supplierId, T0.AddSeconds(second))).IsAllowed);
        }

        Assert.True((await RequestAt(supplierId, T0.AddSeconds(60))).IsAllowed);
    }

    [Fact]
    public async Task Limits_ArePerSupplier()
    {
        var noisySupplier = TestData.NewSupplierId();
        var quietSupplier = TestData.NewSupplierId();
        await SendAllowedAsync(noisySupplier, T0, Limit);

        Assert.False((await RequestAt(noisySupplier, T0)).IsAllowed);
        Assert.True((await RequestAt(quietSupplier, T0)).IsAllowed);
    }

    [Fact]
    public async Task ConcurrentRequests_NeverExceedTheLimit()
    {
        var supplierId = TestData.NewSupplierId();

        var decisions = await Task.WhenAll(Enumerable.Range(0, 150).Select(_ => Task.Run(() => RequestAt(supplierId, T0))));

        Assert.Equal(Limit, decisions.Count(d => d.IsAllowed));
        Assert.Equal(50, decisions.Count(d => !d.IsAllowed));
    }

    [Fact]
    public async Task AcceptedAndThrottledRequests_AreCountedInTheSupplierStats()
    {
        var supplierId = TestData.NewSupplierId();
        await SendAllowedAsync(supplierId, T0, Limit);

        for (var i = 0; i < 3; i++)
        {
            await RequestAt(supplierId, T0);
        }

        var stats = (await _repository.GetStatsAsync(supplierId, default))!;
        Assert.Equal(Limit, stats.AcceptedCount);
        Assert.Equal(3, stats.ThrottledCount);
    }

    private Task<ThrottleDecision> RequestAt(string supplierId, DateTime nowUtc) =>
        _repository.TryAcquireRequestSlotAsync(supplierId, nowUtc, Limit, Window, default);

    private async Task SendAllowedAsync(string supplierId, DateTime nowUtc, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Assert.True((await RequestAt(supplierId, nowUtc)).IsAllowed, $"Request {i} at {nowUtc:O} should have been allowed.");
        }
    }
}
