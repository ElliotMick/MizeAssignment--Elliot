using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using MizeReservations.Api.Infrastructure;

namespace MizeReservations.Api.Migrations;

/// <summary>
/// Minimal runner for the hand-written SQL scripts in this folder (Dapper has no migrations of its own).
/// Creates the database if it is missing, then applies - in file-name order and inside one transaction - every
/// embedded *.sql script that is not yet recorded in dbo.SchemaMigrations. An application lock makes it safe for
/// several instances of the service to start (and migrate) at the same time.
/// </summary>
public sealed partial class DatabaseMigrator(ISqlConnectionFactory connectionFactory, ILogger<DatabaseMigrator> logger)
{
    private const string ScriptResourcePrefix = "Migrations.";
    private const int DatabaseAlreadyExistsError = 1801;

    private const string EnsureDatabaseSql = """
        IF DB_ID(@DatabaseName) IS NOT NULL
            SELECT CAST(0 AS BIT);
        ELSE
        BEGIN
            DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName) + N';';
            EXEC sys.sp_executesql @sql;
            SELECT CAST(1 AS BIT);
        END;
        """;

    private const string AcquireMigrationLockSql = """
        DECLARE @lockResult INT;
        EXEC @lockResult = sys.sp_getapplock
            @Resource = N'MizeReservations:schema-migrations', @LockMode = 'Exclusive',
            @LockOwner = 'Transaction', @LockTimeout = 60000;
        IF @lockResult < 0
            THROW 51000, 'Timed out waiting for the schema migration lock.', 1;
        """;

    private const string EnsureJournalSql = """
        IF OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL
            CREATE TABLE dbo.SchemaMigrations
            (
                ScriptName   NVARCHAR(255) NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
                AppliedAtUtc DATETIME2(7)  NOT NULL CONSTRAINT DF_SchemaMigrations_AppliedAtUtc DEFAULT (SYSUTCDATETIME())
            );
        """;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseExistsAsync(cancellationToken);

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, AcquireMigrationLockSql, cancellationToken: cancellationToken);
        await ExecuteAsync(connection, transaction, EnsureJournalSql, cancellationToken: cancellationToken);

        var applied = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT ScriptName FROM dbo.SchemaMigrations;", transaction: transaction, cancellationToken: cancellationToken)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (scriptName, script) in LoadScripts())
        {
            if (applied.Contains(scriptName))
            {
                continue;
            }

            foreach (var batch in SplitIntoBatches(script))
            {
                await ExecuteAsync(connection, transaction, batch, cancellationToken: cancellationToken);
            }

            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.SchemaMigrations (ScriptName) VALUES (@ScriptName);", new { ScriptName = scriptName }, cancellationToken);

            logger.LogInformation("Applied migration {ScriptName}", scriptName);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionFactory.ConnectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("The connection string must name a database (Database / Initial Catalog).");
        }

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);

        try
        {
            var created = await connection.QuerySingleAsync<bool>(new CommandDefinition(
                EnsureDatabaseSql, new { DatabaseName = databaseName }, cancellationToken: cancellationToken));

            if (created)
            {
                logger.LogInformation("Created database {DatabaseName}", databaseName);
            }
        }
        catch (SqlException ex) when (ex.Number == DatabaseAlreadyExistsError)
        {
            // Another instance created it between our check and our CREATE DATABASE - nothing to do.
        }
    }

    private static Task<int> ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));

    /// <summary>Embedded migration scripts (see the csproj), ordered by file name: 001_..., 002_..., ...</summary>
    private static IEnumerable<(string Name, string Script)> LoadScripts()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ScriptResourcePrefix, StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            yield return (resourceName[ScriptResourcePrefix.Length..], reader.ReadToEnd());
        }
    }

    /// <summary>
    /// "GO" is a sqlcmd/SSMS batch separator, not T-SQL, so scripts that use it are split and sent batch by batch.
    /// </summary>
    private static IEnumerable<string> SplitIntoBatches(string script) =>
        BatchSeparator().Split(script).Where(batch => !string.IsNullOrWhiteSpace(batch));

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();
}
