using Microsoft.Data.SqlClient;

namespace MizeReservations.Api.Infrastructure;

public sealed class SqlConnectionFactory(string connectionString) : ISqlConnectionFactory
{
    public string ConnectionString { get; } = connectionString;

    public SqlConnection CreateConnection() => new(ConnectionString);
}
