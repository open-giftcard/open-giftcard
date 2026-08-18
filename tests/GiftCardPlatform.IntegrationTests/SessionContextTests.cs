using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Infrastructure tests for the transaction-scoped PostgreSQL session context
/// (ADR-020).
///
/// No tenant-owned tables exist yet, so there is no RLS policy to exercise
/// end-to-end. What can be proven now — and what actually protects tenants once
/// those tables arrive — is that the mechanism is transaction-local and cannot
/// leak one caller's context onto a pooled connection reused by the next.
/// The full tenant-isolation test is deferred to the next task, which
/// introduces the first tenant-owned table.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class SessionContextTests(PlatformApiFixture fixture)
{
    private static MutableExecutionContext PlatformOperatorContext(Guid userId)
    {
        var context = new MutableExecutionContext();
        context.SetPlatformOperator(userId, ["platform.organizations.create"]);
        return context;
    }

    private static async Task<string?> ReadSettingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string setting)
    {
        // current_setting(..., missing_ok: true) returns NULL when never set,
        // and an empty string once it has been set and reset.
        await using var command = new NpgsqlCommand("select current_setting(@setting, true)", connection, transaction);
        command.Parameters.AddWithValue("setting", setting);

        var value = await command.ExecuteScalarAsync();
        return value as string;
    }

    [Fact]
    public async Task Session_context_is_visible_inside_the_transaction()
    {
        var userId = Guid.CreateVersion7();
        await using var scopedConnection = new ScopedDatabaseConnection(fixture.AppConnectionString);
        var connection = await scopedConnection.OpenAsync(CancellationToken.None);

        await using var transaction = await connection.BeginTransactionAsync();
        await new SessionContextWriter()
            .WriteAsync(connection, transaction, PlatformOperatorContext(userId), CancellationToken.None);

        Assert.Equal(
            userId.ToString(),
            await ReadSettingAsync(connection, transaction, SessionContextWriter.UserIdSetting));

        Assert.Equal(
            "true",
            await ReadSettingAsync(connection, transaction, SessionContextWriter.PlatformOperatorSetting));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Session_context_does_not_survive_the_transaction()
    {
        var userId = Guid.CreateVersion7();
        await using var scopedConnection = new ScopedDatabaseConnection(fixture.AppConnectionString);
        var connection = await scopedConnection.OpenAsync(CancellationToken.None);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new SessionContextWriter()
                .WriteAsync(connection, transaction, PlatformOperatorContext(userId), CancellationToken.None);

            await transaction.CommitAsync();
        }

        // Same physical connection, after the transaction ended.
        var leaked = await ReadSettingAsync(connection, transaction: null, SessionContextWriter.UserIdSetting);

        Assert.True(
            string.IsNullOrEmpty(leaked),
            $"Session context leaked past its transaction: '{leaked}'.");
    }

    [Fact]
    public async Task A_pooled_connection_cannot_reuse_a_previous_callers_context()
    {
        var firstUserId = Guid.CreateVersion7();

        // First caller sets context, then returns its connection to the pool.
        await using (var first = new ScopedDatabaseConnection(fixture.AppConnectionString))
        {
            var connection = await first.OpenAsync(CancellationToken.None);
            await using var transaction = await connection.BeginTransactionAsync();

            await new SessionContextWriter()
                .WriteAsync(connection, transaction, PlatformOperatorContext(firstUserId), CancellationToken.None);

            await transaction.CommitAsync();
        }

        // Second caller opens a connection from the same pool and reads before
        // writing any context of its own.
        await using var second = new ScopedDatabaseConnection(fixture.AppConnectionString);
        var reused = await second.OpenAsync(CancellationToken.None);

        var observed = await ReadSettingAsync(reused, transaction: null, SessionContextWriter.UserIdSetting);

        Assert.True(
            string.IsNullOrEmpty(observed),
            $"A pooled connection exposed the previous caller's context: '{observed}'.");
        Assert.NotEqual(firstUserId.ToString(), observed);
    }

    [Fact]
    public async Task Anonymous_context_writes_empty_values_rather_than_a_stale_user()
    {
        await using var scopedConnection = new ScopedDatabaseConnection(fixture.AppConnectionString);
        var connection = await scopedConnection.OpenAsync(CancellationToken.None);

        var anonymous = new MutableExecutionContext();
        anonymous.SetAnonymous();

        await using var transaction = await connection.BeginTransactionAsync();
        await new SessionContextWriter().WriteAsync(connection, transaction, anonymous, CancellationToken.None);

        Assert.Equal(string.Empty, await ReadSettingAsync(connection, transaction, SessionContextWriter.UserIdSetting));
        Assert.Equal("false", await ReadSettingAsync(connection, transaction, SessionContextWriter.PlatformOperatorSetting));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Pos_numeric_candidate_is_transaction_local_and_exact()
    {
        var context = new MutableExecutionContext();
        context.SetPosClient(Guid.CreateVersion7(), Guid.CreateVersion7());
        var hash = new string('A', 64);
        context.SetPaymentCodeCandidate(hash);

        await using var scopedConnection = new ScopedDatabaseConnection(fixture.AppConnectionString);
        var connection = await scopedConnection.OpenAsync(CancellationToken.None);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new SessionContextWriter()
                .WriteAsync(connection, transaction, context, CancellationToken.None);

            Assert.Equal(
                hash,
                await ReadSettingAsync(
                    connection,
                    transaction,
                    SessionContextWriter.PaymentCodeHashSetting));

            await transaction.CommitAsync();
        }

        Assert.True(string.IsNullOrEmpty(await ReadSettingAsync(
            connection,
            transaction: null,
            SessionContextWriter.PaymentCodeHashSetting)));
    }
}
