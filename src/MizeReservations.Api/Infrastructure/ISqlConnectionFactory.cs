using Microsoft.Data.SqlClient;

namespace MizeReservations.Api.Infrastructure;

/// <summary>Creates connections to the reservations database.</summary>
public interface ISqlConnectionFactory
{
    string ConnectionString { get; }

    /// <summary>Returns a new, not yet opened connection. Callers own and dispose it.</summary>
    SqlConnection CreateConnection();
}
