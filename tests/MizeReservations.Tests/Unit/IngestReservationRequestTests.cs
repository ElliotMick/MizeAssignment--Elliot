using System.ComponentModel.DataAnnotations;
using MizeReservations.Api.Models;
using MizeReservations.Api.Models.Dtos;

namespace MizeReservations.Tests.Unit;

/// <summary>
/// The request DTO: field presence ([ApiController] validation), the business rules of the reservations table
/// (<see cref="IngestReservationRequest.FindRuleViolations"/>) and the mapping to the domain model.
/// </summary>
public sealed class IngestReservationRequestTests
{
    [Fact]
    public void SamplePayload_IsValid()
    {
        Assert.Empty(Validate(TestData.NewRequest()));
    }

    [Theory]
    [InlineData(nameof(IngestReservationRequest.SupplierId))]
    [InlineData(nameof(IngestReservationRequest.ReservationId))]
    [InlineData(nameof(IngestReservationRequest.RoomId))]
    [InlineData(nameof(IngestReservationRequest.CheckIn))]
    [InlineData(nameof(IngestReservationRequest.CheckOut))]
    [InlineData(nameof(IngestReservationRequest.Price))]
    [InlineData(nameof(IngestReservationRequest.UpdatedAtUtc))]
    public void MissingField_IsInvalid(string field)
    {
        var request = field switch
        {
            nameof(IngestReservationRequest.SupplierId) => TestData.NewRequest() with { SupplierId = null },
            nameof(IngestReservationRequest.ReservationId) => TestData.NewRequest() with { ReservationId = null },
            nameof(IngestReservationRequest.RoomId) => TestData.NewRequest() with { RoomId = null },
            nameof(IngestReservationRequest.CheckIn) => TestData.NewRequest() with { CheckIn = null },
            nameof(IngestReservationRequest.CheckOut) => TestData.NewRequest() with { CheckOut = null },
            nameof(IngestReservationRequest.Price) => TestData.NewRequest() with { Price = null },
            _ => TestData.NewRequest() with { UpdatedAtUtc = null }
        };

        var error = Assert.Single(Validate(request));
        Assert.Contains(field, error.MemberNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSupplierId_IsInvalid(string supplierId)
    {
        Assert.NotEmpty(Validate(TestData.NewRequest() with { SupplierId = supplierId }));
    }

    [Fact]
    public void SamplePayload_BreaksNoBusinessRule()
    {
        Assert.Empty(TestData.NewRequest().FindRuleViolations());
    }

    [Theory]
    [InlineData("checkOut equals checkIn", "checkOut")]
    [InlineData("checkOut before checkIn", "checkOut")]
    [InlineData("negative price", "price")]
    [InlineData("price with 3 decimals", "price")]
    [InlineData("price above DECIMAL(18,2)", "price")]
    [InlineData("supplierId over 100 chars", "supplierId")]
    [InlineData("reservationId over 100 chars", "reservationId")]
    [InlineData("roomId over 100 chars", "roomId")]
    [InlineData("blank roomId", "roomId")]
    [InlineData("missing updatedAtUtc", "updatedAtUtc")]
    public void RequestBreakingATableRule_IsReportedUnderThatField(string scenario, string field)
    {
        var tooLong = new string('x', Reservation.MaxIdLength + 1);
        var request = scenario switch
        {
            "checkOut equals checkIn" => TestData.NewRequest(checkOutUtc: TestData.CheckIn),
            "checkOut before checkIn" => TestData.NewRequest(checkOutUtc: TestData.CheckIn.AddDays(-1)),
            "negative price" => TestData.NewRequest(price: -0.01m),
            "price with 3 decimals" => TestData.NewRequest(price: 450.001m),
            "price above DECIMAL(18,2)" => TestData.NewRequest(price: Reservation.MaxPrice + 1),
            "supplierId over 100 chars" => TestData.NewRequest(supplierId: tooLong),
            "reservationId over 100 chars" => TestData.NewRequest(reservationId: tooLong),
            "roomId over 100 chars" => TestData.NewRequest(roomId: tooLong),
            "blank roomId" => TestData.NewRequest(roomId: " "),
            "missing updatedAtUtc" => TestData.NewRequest() with { UpdatedAtUtc = null },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var violations = request.FindRuleViolations();

        Assert.Equal(new[] { field }, violations.Keys);
    }

    [Fact]
    public void IdsAreMeasuredAfterTrimming()
    {
        var paddedButShortEnough = " " + new string('x', Reservation.MaxIdLength) + " ";

        Assert.Empty(TestData.NewRequest(reservationId: paddedButShortEnough).FindRuleViolations());
    }

    [Fact]
    public void EveryBrokenRule_IsReported_NotJustTheFirst()
    {
        var violations = TestData.NewRequest(price: -1.005m, checkOutUtc: TestData.CheckIn).FindRuleViolations();

        Assert.Single(violations["checkOut"]);
        Assert.Equal(2, violations["price"].Length); // negative and too many decimals
    }

    [Fact]
    public void ToReservation_TrimsIds_AndConvertsEveryInstantToUtc()
    {
        var request = TestData.NewRequest() with
        {
            SupplierId = "  acme ",
            ReservationId = " R-1 ",
            RoomId = " room-101 ",
            CheckIn = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            UpdatedAtUtc = new DateTimeOffset(2026, 7, 1, 5, 15, 0, TimeSpan.FromHours(-5))
        };

        var reservation = request.ToReservation();

        Assert.Equal("acme", reservation.SupplierId);
        Assert.Equal("R-1", reservation.ReservationId);
        Assert.Equal("room-101", reservation.RoomId);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), reservation.CheckInUtc);
        Assert.Equal(DateTimeKind.Utc, reservation.CheckInUtc.Kind);
        Assert.Equal(new DateTime(2026, 7, 1, 10, 15, 0, DateTimeKind.Utc), reservation.UpdatedAtUtc);
    }

    /// <summary>Runs the same DataAnnotations validation that [ApiController] runs on the request body.</summary>
    private static List<ValidationResult> Validate(IngestReservationRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }
}
