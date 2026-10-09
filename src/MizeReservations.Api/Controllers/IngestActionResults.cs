using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Net.Http.Headers;
using MizeReservations.Api.Models;
using MizeReservations.Api.Models.Dtos;

namespace MizeReservations.Api.Controllers;

/// <summary>Turns the result of an ingest into its HTTP response (status code, headers and body).</summary>
internal static class IngestActionResults
{
    public static IActionResult ToActionResult(this ControllerBase controller, IngestResult result, IngestReservationRequest request)
    {
        if (result.Outcome == IngestOutcome.Invalid)
        {
            return controller.BrokenBusinessRules(result.ValidationErrors!);
        }

        // Every other outcome means the request passed the business rules, so it maps cleanly (trimmed IDs).
        var reservation = request.ToReservation();
        return result.Outcome switch
        {
            IngestOutcome.Created => controller.StatusCode(
                StatusCodes.Status201Created, IngestReservationResponse.For(reservation, result.Outcome)),
            IngestOutcome.Updated or IngestOutcome.Unchanged => controller.Ok(IngestReservationResponse.For(reservation, result.Outcome)),
            IngestOutcome.Stale => controller.StaleVersion(reservation, result.StoredUpdatedAtUtc!.Value),
            IngestOutcome.Throttled => controller.TooManyRequests(reservation, result.RetryAfter!.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unknown ingest outcome.")
        };
    }

    /// <summary>400 in the same shape as the automatic model validation errors, with <c>outcome: "Invalid"</c>.</summary>
    private static ObjectResult BrokenBusinessRules(this ControllerBase controller, IReadOnlyDictionary<string, string[]> errors)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (field, messages) in errors)
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(field, message);
            }
        }

        var problem = controller.ProblemDetailsFactory.CreateValidationProblemDetails(
            controller.HttpContext, modelState, StatusCodes.Status400BadRequest, "The reservation breaks one or more business rules.");
        problem.Extensions["outcome"] = IngestOutcome.Invalid;

        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }

    private static ObjectResult StaleVersion(this ControllerBase controller, Reservation reservation, DateTime storedUpdatedAtUtc) =>
        controller.CreateProblem(
            StatusCodes.Status409Conflict,
            "Stale reservation version",
            $"A newer version of reservation '{reservation.ReservationId}' (updatedAtUtc {storedUpdatedAtUtc:O}) is already stored; " +
            "this update was not applied.",
            new Dictionary<string, object?>
            {
                ["supplierId"] = reservation.SupplierId,
                ["reservationId"] = reservation.ReservationId,
                ["outcome"] = IngestOutcome.Stale,
                ["storedUpdatedAtUtc"] = storedUpdatedAtUtc
            });

    private static ObjectResult TooManyRequests(this ControllerBase controller, Reservation reservation, TimeSpan retryAfter)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        controller.Response.Headers[HeaderNames.RetryAfter] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        return controller.CreateProblem(
            StatusCodes.Status429TooManyRequests,
            "Too many requests",
            $"Supplier '{reservation.SupplierId}' exceeded its request limit. Retry after {retryAfterSeconds} second(s).",
            new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfterSeconds },
            type: "https://www.rfc-editor.org/rfc/rfc6585#section-4");
    }

    private static ObjectResult CreateProblem(
        this ControllerBase controller, int statusCode, string title, string detail, IDictionary<string, object?> extensions, string? type = null)
    {
        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(controller.HttpContext, statusCode, title, type, detail);
        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        return new ObjectResult(problem) { StatusCode = statusCode };
    }
}
