using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MizeReservations.Api.Infrastructure;
using MizeReservations.Api.Models;
using MizeReservations.Api.Models.Dtos;
using MizeReservations.Api.Repository;
using MizeReservations.Api.Services;
using Moq;

namespace MizeReservations.Tests.Unit;

/// <summary>
/// Every path an ingest request can take through <see cref="ReservationService"/>, with the repository mocked:
/// the business-rule gate, the idempotency decisions, the races against concurrent requests and the throttle.
/// (The rules themselves are covered in <see cref="IngestReservationRequestTests"/>.)
/// </summary>
public sealed class ReservationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] VersionA = [0, 0, 0, 0, 0, 0, 0, 1];
    private static readonly byte[] VersionB = [0, 0, 0, 0, 0, 0, 0, 2];

    /// <summary>GetAsync result for a reservation that is not stored.</summary>
    private static readonly Reservation? NotStored = null;

    private readonly Mock<IReservationRepository> _repository = new();
    private readonly ReservationService _service;

    public ReservationServiceTests()
    {
        _repository
            .Setup(r => r.TryAcquireRequestSlotAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThrottleDecision.Allowed);

        _service = CreateService(new IngestOptions());
    }

    [Fact]
    public async Task NewReservation_IsInsertedInOneRoundTrip_AndReportedAsCreated()
    {
        var request = TestData.NewRequest();
        SetupInsertSucceeds(true);

        var result = await _service.IngestAsync(request, default);

        Assert.Equal(IngestOutcome.Created, result.Outcome);
        _repository.Verify(r => r.TryInsertAsync(
            It.Is<Reservation>(stored => stored.ReservationId == "res-1" && stored.HasSameDetailsAs(request.ToReservation())),
            Now.UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoUpdate();
        VerifyOutcomeRecorded(IngestOutcome.Created);
    }

    [Fact]
    public async Task ExactResend_IsRejectedByTheKey_AndReportedAsUnchanged_WithoutAnUpdate()
    {
        SetupInsertSucceeds(false);
        SetupStoredVersions(TestData.NewReservation(rowVersion: VersionA));

        var result = await _service.IngestAsync(TestData.NewRequest(), default);

        Assert.Equal(IngestOutcome.Unchanged, result.Outcome);
        VerifyNoUpdate();
        VerifyOutcomeRecorded(IngestOutcome.Unchanged);
    }

    [Fact]
    public async Task Resend_WithTheSameInstantsInAnotherUtcOffset_IsUnchanged()
    {
        SetupInsertSucceeds(false);
        SetupStoredVersions(TestData.NewReservation(rowVersion: VersionA));
        var request = TestData.NewRequest() with
        {
            CheckIn = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            CheckOut = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.FromHours(2)),
            UpdatedAtUtc = new DateTimeOffset(2026, 7, 1, 12, 15, 0, TimeSpan.FromHours(2))
        };

        var result = await _service.IngestAsync(request, default);

        Assert.Equal(IngestOutcome.Unchanged, result.Outcome);
        VerifyNoUpdate();
    }

    [Fact]
    public async Task ChangedDetails_WithANewerVersion_AreUpdated_GuardedByTheRowVersionThatWasRead()
    {
        SetupInsertSucceeds(false);
        SetupStoredVersions(TestData.NewReservation(rowVersion: VersionA));
        SetupUpdateSucceeds(true);

        var result = await _service.IngestAsync(TestData.NewRequest(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1)), default);

        Assert.Equal(IngestOutcome.Updated, result.Outcome);
        _repository.Verify(r => r.TryUpdateAsync(
            It.Is<Reservation>(stored => stored.Price == 500m), VersionA, Now.UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
        VerifyOutcomeRecorded(IngestOutcome.Updated);
    }

    [Fact]
    public async Task ChangedDetails_WithTheSameVersionTimestamp_AreUpdated()
    {
        // Same updatedAtUtc but a different price: the timestamps cannot order the two, so the last arrival wins.
        SetupInsertSucceeds(false);
        SetupStoredVersions(TestData.NewReservation(rowVersion: VersionA));
        SetupUpdateSucceeds(true);

        var result = await _service.IngestAsync(TestData.NewRequest(price: 500m), default);

        Assert.Equal(IngestOutcome.Updated, result.Outcome);
    }

    [Fact]
    public async Task OlderVersion_IsNotWritten_AndReportedAsStale()
    {
        var newerUpdatedAt = TestData.UpdatedAt.AddHours(1);
        SetupInsertSucceeds(false);
        SetupStoredVersions(TestData.NewReservation(price: 500m, updatedAtUtc: newerUpdatedAt, rowVersion: VersionA));

        var result = await _service.IngestAsync(TestData.NewRequest(), default);

        Assert.Equal(IngestOutcome.Stale, result.Outcome);
        Assert.Equal(newerUpdatedAt, result.StoredUpdatedAtUtc);
        VerifyNoUpdate();
        VerifyOutcomeRecorded(IngestOutcome.Stale);
    }

    [Fact]
    public async Task UpdateRace_LostToANewerVersion_EndsAsStale()
    {
        SetupInsertSucceeds(false);
        SetupStoredVersions(
            TestData.NewReservation(rowVersion: VersionA),
            TestData.NewReservation(price: 600m, updatedAtUtc: TestData.UpdatedAt.AddHours(2), rowVersion: VersionB));
        SetupUpdateSucceeds(false);

        var result = await _service.IngestAsync(TestData.NewRequest(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1)), default);

        Assert.Equal(IngestOutcome.Stale, result.Outcome);
        _repository.Verify(r => r.TryUpdateAsync(It.IsAny<Reservation>(), VersionA, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        VerifyOutcomeRecorded(IngestOutcome.Stale);
    }

    [Fact]
    public async Task UpdateRace_LostToAnIdenticalRequest_EndsAsUnchanged()
    {
        SetupInsertSucceeds(false);
        SetupStoredVersions(
            TestData.NewReservation(rowVersion: VersionA),
            TestData.NewReservation(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1), rowVersion: VersionB));
        SetupUpdateSucceeds(false);

        var result = await _service.IngestAsync(TestData.NewRequest(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1)), default);

        Assert.Equal(IngestOutcome.Unchanged, result.Outcome);
    }

    [Fact]
    public async Task RowDeletedAfterTheInsertWasRejected_IsInsertedAgain()
    {
        _repository
            .SetupSequence(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);
        SetupStoredVersions(NotStored);

        var result = await _service.IngestAsync(TestData.NewRequest(), default);

        Assert.Equal(IngestOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task ConflictThatNeverResolves_GivesUpAfterTheConfiguredAttempts_AndIsCountedAsFailed()
    {
        var service = CreateService(new IngestOptions { MaxWriteAttempts = 3 });
        SetupInsertSucceeds(false);
        SetupStoredVersions(TestData.NewReservation(rowVersion: VersionA));
        SetupUpdateSucceeds(false);

        await Assert.ThrowsAsync<DBConcurrencyException>(() =>
            service.IngestAsync(TestData.NewRequest(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1)), default));

        // The insert is tried once (the row exists); each of the 3 attempts then loses its update race.
        _repository.Verify(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(
            r => r.TryUpdateAsync(It.IsAny<Reservation>(), It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        VerifyOutcomeRecorded(IngestOutcome.Failed);
    }

    [Fact]
    public async Task StoreFailure_IsCountedAsFailed_AndTheErrorStillReachesTheCaller()
    {
        _repository
            .Setup(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("SQL timeout"));

        await Assert.ThrowsAsync<TimeoutException>(() => _service.IngestAsync(TestData.NewRequest(), default));

        VerifyOutcomeRecorded(IngestOutcome.Failed);
    }

    [Fact]
    public async Task Outcome_IsRecordedEvenIfTheClientHasDisconnected()
    {
        using var clientDisconnected = new CancellationTokenSource();
        _repository
            .Setup(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback(clientDisconnected.Cancel) // the client hangs up right after the reservation is stored
            .ReturnsAsync(true);

        await _service.IngestAsync(TestData.NewRequest(), clientDisconnected.Token);

        _repository.Verify(r => r.RecordOutcomeAsync(
            "supplier-1", IngestOutcome.Created, It.IsAny<DateTime>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task StatsWriteFailure_DoesNotFailAReservationThatWasStored()
    {
        SetupInsertSucceeds(true);
        _repository
            .Setup(r => r.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<IngestOutcome>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("SQL timeout"));

        var result = await _service.IngestAsync(TestData.NewRequest(), default);

        Assert.Equal(IngestOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task RequestBreakingABusinessRule_IsInvalid_AndNeverReachesTheDatabase()
    {
        var result = await _service.IngestAsync(TestData.NewRequest(price: -1m, checkOutUtc: TestData.CheckIn), default);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);
        Assert.Equal(["checkOut", "price"], result.ValidationErrors!.Keys.Order());
        _repository.VerifyNoOtherCalls(); // not even the throttle: an invalid request is not counted against the supplier
    }

    [Fact]
    public async Task ThrottledRequest_ReturnsRetryAfter_AndNeverTouchesReservations()
    {
        _repository
            .Setup(r => r.TryAcquireRequestSlotAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThrottleDecision.Rejected(TimeSpan.FromSeconds(42)));

        var result = await _service.IngestAsync(TestData.NewRequest(), default);

        Assert.Equal(IngestOutcome.Throttled, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);
        _repository.Verify(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoUpdate();
        // Throttled requests are counted inside TryAcquireRequestSlotAsync, not by RecordOutcomeAsync.
        _repository.Verify(r => r.RecordOutcomeAsync(
            It.IsAny<string>(), It.IsAny<IngestOutcome>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ThrottleCheck_UsesTheTrimmedSupplierId_ConfiguredLimitWindow_AndClock()
    {
        SetupInsertSucceeds(true);

        await _service.IngestAsync(TestData.NewRequest(supplierId: " acme "), default);

        _repository.Verify(r => r.TryAcquireRequestSlotAsync(
            "acme", Now.UtcDateTime, 100, TimeSpan.FromSeconds(60), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stats_OfAnUnknownSupplier_AreAllZero()
    {
        _repository.Setup(r => r.GetStatsAsync("unknown", It.IsAny<CancellationToken>())).ReturnsAsync((SupplierStats?)null);

        var stats = await _service.GetStatsAsync("unknown", default);

        Assert.Equal("unknown", stats.SupplierId);
        Assert.Equal(0, stats.AcceptedCount);
        Assert.Equal(0, stats.ThrottledCount);
    }

    [Fact]
    public async Task Stats_ReportAcceptedRequestsAsIngested_WithHowEachEnded()
    {
        _repository.Setup(r => r.GetStatsAsync("acme", It.IsAny<CancellationToken>())).ReturnsAsync(new SupplierStats
        {
            SupplierId = "acme", AcceptedCount = 12, ThrottledCount = 7,
            CreatedCount = 5, UpdatedCount = 3, UnchangedCount = 2, StaleCount = 1, FailedCount = 1
        });

        var response = SupplierStatsResponse.FromModel(await _service.GetStatsAsync("acme", default));

        Assert.Equal(12, response.Ingested);
        Assert.Equal(7, response.Throttled);
        Assert.Equal(new IngestBreakdown(Created: 5, Updated: 3, Unchanged: 2, Stale: 1, Failed: 1), response.Breakdown);
    }

    private ReservationService CreateService(IngestOptions ingestOptions) => new(
        _repository.Object,
        new FakeTimeProvider(Now),
        Options.Create(new ThrottlingOptions()),
        Options.Create(ingestOptions),
        NullLogger<ReservationService>.Instance);

    /// <summary>Successive GetAsync calls return these rows in order (null = not stored); the last one repeats.</summary>
    private void SetupStoredVersions(params Reservation?[] versions)
    {
        var call = 0;
        _repository
            .Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => versions[Math.Min(call++, versions.Length - 1)]);
    }

    private void SetupInsertSucceeds(bool succeeds) =>
        _repository
            .Setup(r => r.TryInsertAsync(It.IsAny<Reservation>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(succeeds);

    private void SetupUpdateSucceeds(bool succeeds) =>
        _repository
            .Setup(r => r.TryUpdateAsync(It.IsAny<Reservation>(), It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(succeeds);

    private void VerifyNoUpdate() =>
        _repository.Verify(
            r => r.TryUpdateAsync(It.IsAny<Reservation>(), It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private void VerifyOutcomeRecorded(IngestOutcome outcome) =>
        _repository.Verify(r => r.RecordOutcomeAsync("supplier-1", outcome, Now.UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
}
