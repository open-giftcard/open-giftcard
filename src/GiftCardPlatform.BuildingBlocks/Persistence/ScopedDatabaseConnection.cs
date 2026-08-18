using Npgsql;

namespace GiftCardPlatform.BuildingBlocks.Persistence;

/// <summary>
/// Owns the single physical Npgsql connection shared by every module DbContext
/// within one execution scope (ADR-011). Sharing one connection is what allows
/// separate module DbContexts to enlist in the same PostgreSQL transaction.
/// </summary>
public sealed class ScopedDatabaseConnection : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;

    public ScopedDatabaseConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connection = new NpgsqlConnection(connectionString);
    }

    /// <summary>
    /// The shared connection instance. Handed to each module DbContext at
    /// configuration time; it may not yet be open.
    /// </summary>
    public NpgsqlConnection Connection => _connection;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return _connection;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
