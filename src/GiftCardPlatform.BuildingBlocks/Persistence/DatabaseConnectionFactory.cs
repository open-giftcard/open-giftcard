using Npgsql;

namespace GiftCardPlatform.BuildingBlocks.Persistence;

/// <summary>
/// Creates connections independent of the request-scoped
/// <see cref="ScopedDatabaseConnection"/>.
///
/// Needed where work must survive the rollback of the current unit of work — a
/// failure audit record, for example, cannot be written on the scoped connection
/// because that connection is inside the very transaction being rolled back
/// (ADR-025).
/// </summary>
public interface IDatabaseConnectionFactory
{
    Task<NpgsqlConnection> CreateOpenAsync(CancellationToken cancellationToken);
}

public sealed class DatabaseConnectionFactory(string connectionString) : IDatabaseConnectionFactory
{
    public async Task<NpgsqlConnection> CreateOpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
