using System.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace MizeReservations.Api.Infrastructure;

/// <summary>
/// Turns exceptions that escape a request into RFC 7807 problem responses without leaking internals.
/// (The exception itself is already logged by the exception handler middleware.)
/// </summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            // ReservationService gave up after repeatedly losing races on the same reservation: transient, retrying works.
            DBConcurrencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Concurrent modification",
                Detail = "The reservation was being modified concurrently. Please retry the request."
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred."
            }
        };

        if (problem.Status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers[HeaderNames.RetryAfter] = "1";
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return problemDetailsService.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
