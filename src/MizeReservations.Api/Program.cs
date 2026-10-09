using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using MizeReservations.Api.Infrastructure;
using MizeReservations.Api.Migrations;
using MizeReservations.Api.Repository;
using MizeReservations.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(options =>
        // Report validation errors under the JSON names suppliers send ("checkOut"), not the C# names ("CheckOut").
        options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOptions<ThrottlingOptions>()
    .Bind(builder.Configuration.GetSection(ThrottlingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<IngestOptions>()
    .Bind(builder.Configuration.GetSection(IngestOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
// Resolved lazily so that configuration overrides (e.g. from the integration tests) are honoured.
builder.Services.AddSingleton<ISqlConnectionFactory>(services => new SqlConnectionFactory(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Reservations")
    ?? throw new InvalidOperationException("Connection string 'Reservations' is not configured.")));
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddScoped<IReservationRepository, ReservationRepository>();
builder.Services.AddScoped<IReservationService, ReservationService>();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();

// Makes the implicit Program class visible to WebApplicationFactory in the integration tests.
public partial class Program { }
