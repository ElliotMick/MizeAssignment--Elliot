using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MizeReservations.Api.Infrastructure;

namespace MizeReservations.Tests.Unit;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task ConcurrencyConflict_Becomes503_WithRetryAfter()
    {
        var (context, body) = await HandleAsync(new DBConcurrencyException("kept changing"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("Concurrent modification", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnexpectedException_Becomes500_WithoutLeakingItsMessage()
    {
        var (context, body) = await HandleAsync(new InvalidOperationException("Login failed for user 'sa' on server db-prod-01"));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.DoesNotContain("db-prod-01", body.GetRawText());
    }

    private static async Task<(HttpContext Context, JsonElement Body)> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var handler = new GlobalExceptionHandler(services.GetRequiredService<IProblemDetailsService>());
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context, document.RootElement.Clone());
    }
}
