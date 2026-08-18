using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// A raw SQL session on the runtime application role with an RLS session context
/// established, for tests that verify database state directly.
///
/// Tenant-owned tables are behind RLS, so a bare connection with no session
/// context sees nothing at all. That is the policy working: a verification query
/// has to say which tenant it is acting as, just as the application does.
/// </summary>
internal sealed class ScopedSqlSession : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _committed;

    private ScopedSqlSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<ScopedSqlSession> OpenAsync(
        PlatformApiFixture fixture,
        Guid? organizationId,
        bool isPlatformOperator,
        Guid? userId = null)
    {
        var connection = await fixture.OpenAppConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();

        await MembershipTestSupport.SetSessionContextAsync(
            connection,
            transaction,
            organizationId,
            isPlatformOperator,
            userId);

        return new ScopedSqlSession(connection, transaction);
    }

    /// <summary>Acts as a platform operator, which reads across tenants.</summary>
    public static Task<ScopedSqlSession> OpenAsPlatformAsync(PlatformApiFixture fixture) =>
        OpenAsync(fixture, organizationId: null, isPlatformOperator: true);

    /// <summary>Acts as a caller scoped to a single customer organization.</summary>
    public static Task<ScopedSqlSession> OpenAsOrganizationAsync(PlatformApiFixture fixture, Guid organizationId) =>
        OpenAsync(fixture, organizationId, isPlatformOperator: false);

    /// <summary>Acts as an authenticated identity with no organization membership.</summary>
    public static Task<ScopedSqlSession> OpenAsIdentityAsync(
        PlatformApiFixture fixture,
        Guid userId) =>
        OpenAsync(
            fixture,
            organizationId: null,
            isPlatformOperator: false,
            userId);

    public NpgsqlCommand Command(string sql) => new(sql, _connection, _transaction);

    public async Task<long> ScalarCountAsync(string sql, Action<NpgsqlCommand>? configure = null)
    {
        await using var command = Command(sql);
        configure?.Invoke(command);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task CommitAsync()
    {
        await _transaction.CommitAsync();
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // Already completed.
            }
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
