using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MizeReservations.Api.Infrastructure;
using MizeReservations.Api.Migrations;

namespace MizeReservations.Tests.Integration;

/// <summary>
/// Creates a throw-away database, builds the schema with the real migration scripts (through
/// <see cref="DatabaseMigrator"/>) and drops the database when the test run ends. Uses LocalDB by default;
/// set MIZE_TEST_SQLSERVER to a server-level connection string to use another SQL Server.
/// </summary>
public sealed class LocalDbFixture : IAsyncLifetime
{
    private const string DefaultServer = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    public LocalDbFixture()
    {
        ConnectionString = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MIZE_TEST_SQLSERVER") ?? DefaultServer)
        {
            InitialCatalog = DatabaseName,
            ConnectTimeout = 60 // the first LocalDB start-up can be slow
        }.ConnectionString;
    }

    public string DatabaseName { get; } = $"MizeReservations_Tests_{Guid.NewGuid():N}";

    public string ConnectionString { get; }

    public ISqlConnectionFactory ConnectionFactory => new SqlConnectionFactory(ConnectionString);

    public Task InitializeAsync() =>
        new DatabaseMigrator(ConnectionFactory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.ExecuteAsync($"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END;
            """);
    }

    /// <summary>Runs a scalar query against the test database, to assert on what is actually stored.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters);
    }
}

[CollectionDefinition(Name)]
public sealed class LocalDbCollection : ICollectionFixture<LocalDbFixture>
{
    public const string Name = "LocalDb";
}
