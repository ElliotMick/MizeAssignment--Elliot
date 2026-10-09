using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace MizeReservations.Tests.Integration;

/// <summary>
/// One in-memory instance of the API, wired to the test database and a controllable clock. Creating two of them on the
/// same database simulates two server instances behind a load balancer.
/// </summary>
public sealed class ApiFactory(string connectionString, FakeTimeProvider clock) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Reservations", connectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false"); // LocalDbFixture already built the schema
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
    }
}
