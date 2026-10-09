using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace MizeReservations.Tests.Integration;

/// <summary>The HTTP contract, through in-memory instances of the API on the shared test database.</summary>
[Collection(LocalDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReservationsApiTests(LocalDbFixture database) : IDisposable
{
    private const string IngestUrl = "/api/reservations/ingest";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly List<ApiFactory> _instances = [];

    public void Dispose()
    {
        foreach (var instance in _instances)
        {
            instance.Dispose();
        }
    }

    [Fact]
    public async Task Ingest_RespondsCreated_Unchanged_Updated_ThenConflictForADelayedOlderVersion()
    {
        var client = StartInstance();
        var supplierId = TestData.NewSupplierId();

        var created = await client.PostAsJsonAsync(IngestUrl, Payload(supplierId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Created", await OutcomeOf(created));

        var resent = await client.PostAsJsonAsync(IngestUrl, Payload(supplierId));
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal("Unchanged", await OutcomeOf(resent));

        var updated = await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, price: 500m, updatedAtUtc: "2026-07-02T08:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Updated", await OutcomeOf(updated));

        var stale = await client.PostAsJsonAsync(IngestUrl, Payload(supplierId));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType?.MediaType);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Stale", problem.GetProperty("outcome").GetString());
        Assert.Equal(new DateTime(2026, 7, 2, 8, 0, 0, DateTimeKind.Utc), problem.GetProperty("storedUpdatedAtUtc").GetDateTime());

        // The older version did not overwrite the newer one.
        Assert.Equal(500m, await database.ScalarAsync<decimal>(
            "SELECT Price FROM dbo.Reservations WHERE SupplierId = @supplierId AND ReservationId = 'R-1001'", new { supplierId }));
    }

    [Fact]
    public async Task Ingest_BrokenBusinessRules_Returns400NamingEachField()
    {
        var client = StartInstance();
        var payload = new
        {
            supplierId = "acme", reservationId = "R-1", roomId = "room-101",
            checkIn = "2026-08-03T00:00:00Z", checkOut = "2026-08-01T00:00:00Z",
            price = -450.001m, updatedAtUtc = "2026-07-01T10:15:00Z"
        };

        var response = await client.PostAsJsonAsync(IngestUrl, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid", problem.GetProperty("outcome").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("checkOut", out _));
        Assert.Equal(2, problem.GetProperty("errors").GetProperty("price").GetArrayLength());
    }

    [Fact]
    public async Task Ingest_MissingFields_Returns400ListingThem()
    {
        var client = StartInstance();

        var response = await client.PostAsJsonAsync(IngestUrl, new { supplierId = "acme", reservationId = "R-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("roomId", out _));
        Assert.True(errors.TryGetProperty("price", out _));
        Assert.True(errors.TryGetProperty("updatedAtUtc", out _));
    }

    [Fact]
    public async Task Ingest_MalformedJson_Returns400()
    {
        var client = StartInstance();

        var response = await client.PostAsync(IngestUrl, new StringContent("{ \"supplierId\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_OverTheLimit_Returns429WithRetryAfter_UntilTheWindowClears()
    {
        var client = StartInstance();
        var supplierId = TestData.NewSupplierId();

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, $"R-{i}"))).StatusCode);
        }

        var throttled = await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, "R-100"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), throttled.Headers.RetryAfter?.Delta);

        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, "R-100"))).StatusCode);
    }

    [Fact]
    public async Task Stats_ReportIngestedVersusThrottled()
    {
        var client = StartInstance();
        var supplierId = TestData.NewSupplierId();

        await client.PostAsJsonAsync(IngestUrl, Payload(supplierId));                                                     // created
        await client.PostAsJsonAsync(IngestUrl, Payload(supplierId));                                                     // unchanged
        await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, price: 500m, updatedAtUtc: "2026-07-02T08:00:00Z")); // updated
        await client.PostAsJsonAsync(IngestUrl, Payload(supplierId));                                                     // stale (409)
        for (var i = 0; i < 96; i++)
        {
            await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, $"R-{i}"));                                       // created
        }

        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync(IngestUrl, Payload(supplierId, $"X-{i}"));                                       // throttled
        }

        var stats = await client.GetFromJsonAsync<JsonElement>($"/api/reservations/stats/{supplierId}");

        Assert.Equal(100, stats.GetProperty("ingested").GetInt64());
        Assert.Equal(3, stats.GetProperty("throttled").GetInt64());
        var breakdown = stats.GetProperty("breakdown");
        Assert.Equal(97, breakdown.GetProperty("created").GetInt64());
        Assert.Equal(1, breakdown.GetProperty("updated").GetInt64());
        Assert.Equal(1, breakdown.GetProperty("unchanged").GetInt64());
        Assert.Equal(1, breakdown.GetProperty("stale").GetInt64());
        Assert.Equal(0, breakdown.GetProperty("failed").GetInt64());
    }

    [Fact]
    public async Task Stats_OfAnUnknownSupplier_AreZero()
    {
        var client = StartInstance();

        var stats = await client.GetFromJsonAsync<JsonElement>($"/api/reservations/stats/{TestData.NewSupplierId()}");

        Assert.Equal(0, stats.GetProperty("ingested").GetInt64());
        Assert.Equal(0, stats.GetProperty("throttled").GetInt64());
    }

    [Fact]
    public async Task TwoInstances_ShareOneLimitPerSupplier()
    {
        // Two hosts with separate DI containers and memory, like two servers behind a load balancer: only the
        // database is shared. An in-memory rate limiter would let 120 through here (60 per instance).
        var instanceA = StartInstance();
        var instanceB = StartInstance();
        var supplierId = TestData.NewSupplierId();

        var responses = await Task.WhenAll(Enumerable.Range(0, 120).Select(i =>
            (i % 2 == 0 ? instanceA : instanceB).PostAsJsonAsync(IngestUrl, Payload(supplierId, $"R-{i}"))));

        Assert.Equal(100, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    /// <summary>Starts another instance of the API on the shared test database.</summary>
    private HttpClient StartInstance()
    {
        var instance = new ApiFactory(database.ConnectionString, _clock);
        _instances.Add(instance);
        return instance.CreateClient();
    }

    /// <summary>The sample payload from the assignment.</summary>
    private static object Payload(
        string supplierId, string reservationId = "R-1001", decimal price = 450.00m, string updatedAtUtc = "2026-07-01T10:15:00Z") => new
    {
        supplierId,
        reservationId,
        roomId = "room-101",
        checkIn = "2026-08-01T00:00:00Z",
        checkOut = "2026-08-03T00:00:00Z",
        price,
        updatedAtUtc
    };

    private static async Task<string?> OutcomeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString();
}
