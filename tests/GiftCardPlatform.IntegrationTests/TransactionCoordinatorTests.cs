using System.Data;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Nesting and isolation behaviour of the transaction coordinator (ADR-026).
///
/// These matter before the Ledger exists: a value-changing operation will span
/// modules that each own a transaction boundary, and must not permit lost
/// updates.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class TransactionCoordinatorTests(PlatformApiFixture fixture)
{
    private static MutableExecutionContext PlatformContext()
    {
        var context = new MutableExecutionContext();
        context.SetPlatformOperator(Guid.CreateVersion7(), ["platform.organizations.view"]);
        return context;
    }

    private static TransactionCoordinator CreateCoordinator(ScopedDatabaseConnection connection) =>
        new(connection, new SessionContextWriter(), PlatformContext());

    private ScopedDatabaseConnection NewConnection() => new(fixture.AppConnectionString);

    [Fact]
    public async Task A_nested_begin_joins_the_in_progress_transaction()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var outer = await coordinator.BeginAsync(CancellationToken.None);
        Assert.True(outer.IsOutermost);

        await using var inner = await coordinator.BeginAsync(CancellationToken.None);

        Assert.False(inner.IsOutermost);
        // The same physical transaction, not a second one.
        Assert.Same(outer.Transaction, inner.Transaction);
        Assert.Same(outer, coordinator.Current);

        await inner.CommitAsync(CancellationToken.None);
        await outer.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_nested_commit_does_not_end_the_transaction()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var outer = await coordinator.BeginAsync(CancellationToken.None);

        await using (var inner = await coordinator.BeginAsync(CancellationToken.None))
        {
            await inner.CommitAsync(CancellationToken.None);
        }

        // Still usable after the nested scope completed: only the outermost
        // scope ends the transaction.
        await using var command = new NpgsqlCommand("select 1", outer.Transaction.Connection, outer.Transaction);
        Assert.Equal(1, await command.ExecuteScalarAsync());

        await outer.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_abandoned_nested_scope_prevents_the_outer_commit()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var outer = await coordinator.BeginAsync(CancellationToken.None);

        // A nested scope that leaves without completing represents a failed inner
        // operation. The outer scope must not be able to commit a partial unit.
        await using (await coordinator.BeginAsync(CancellationToken.None))
        {
            // deliberately no CommitAsync
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => outer.CommitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_transaction_is_released_once_the_outermost_scope_is_disposed()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using (var outer = await coordinator.BeginAsync(CancellationToken.None))
        {
            await outer.CommitAsync(CancellationToken.None);
        }

        Assert.Null(coordinator.Current);

        // A fresh transaction can be started afterwards.
        await using var next = await coordinator.BeginAsync(CancellationToken.None);
        Assert.True(next.IsOutermost);
        await next.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_requested_isolation_level_reaches_postgresql()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var transaction = await coordinator.BeginAsync(
            IsolationLevel.Serializable, CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select current_setting('transaction_isolation')",
            transaction.Transaction.Connection,
            transaction.Transaction);

        Assert.Equal("serializable", (string)(await command.ExecuteScalarAsync())!);

        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_default_isolation_level_is_read_committed()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var transaction = await coordinator.BeginAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select current_setting('transaction_isolation')",
            transaction.Transaction.Connection,
            transaction.Transaction);

        Assert.Equal("read committed", (string)(await command.ExecuteScalarAsync())!);

        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Joining_a_weaker_transaction_than_requested_is_rejected()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var outer = await coordinator.BeginAsync(
            IsolationLevel.ReadCommitted, CancellationToken.None);

        // Silently handing back a weaker guarantee than the caller asked for is
        // exactly how overspend bugs get written.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.BeginAsync(IsolationLevel.Serializable, CancellationToken.None));

        Assert.Contains("Serializable", exception.Message, StringComparison.Ordinal);

        await outer.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Joining_a_stronger_transaction_than_requested_is_allowed()
    {
        await using var connection = NewConnection();
        var coordinator = CreateCoordinator(connection);

        await using var outer = await coordinator.BeginAsync(
            IsolationLevel.Serializable, CancellationToken.None);

        // A caller wanting ReadCommitted is satisfied by Serializable.
        await using var inner = await coordinator.BeginAsync(
            IsolationLevel.ReadCommitted, CancellationToken.None);

        Assert.False(inner.IsOutermost);

        await inner.CommitAsync(CancellationToken.None);
        await outer.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Serializable_transactions_detect_write_conflicts()
    {
        // Proves the mechanism the Ledger will rely on to prevent overspend:
        // two concurrent serializable transactions reading then writing the same
        // row cannot both succeed.
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);

        await using var firstConnection = NewConnection();
        await using var secondConnection = NewConnection();

        var first = CreateCoordinator(firstConnection);
        var second = CreateCoordinator(secondConnection);

        await using var firstTransaction = await first.BeginAsync(
            IsolationLevel.Serializable, CancellationToken.None);
        await using var secondTransaction = await second.BeginAsync(
            IsolationLevel.Serializable, CancellationToken.None);

        // Both read the same row.
        foreach (var transaction in new[] { firstTransaction, secondTransaction })
        {
            await using var read = new NpgsqlCommand(
                "select name from organizations.organizations where id = @id",
                transaction.Transaction.Connection,
                transaction.Transaction);
            read.Parameters.AddWithValue("id", organizationId);
            await read.ExecuteScalarAsync();
        }

        // Both write it.
        foreach (var (transaction, name) in new[]
                 {
                     (firstTransaction, "First Writer"),
                     (secondTransaction, "Second Writer"),
                 })
        {
            await using var write = new NpgsqlCommand(
                "update organizations.organizations set name = @name where id = @id",
                transaction.Transaction.Connection,
                transaction.Transaction);
            write.Parameters.AddWithValue("name", name);
            write.Parameters.AddWithValue("id", organizationId);

            if (transaction == firstTransaction)
            {
                await write.ExecuteNonQueryAsync();
                continue;
            }

            // The second writer blocks until the first commits, then fails.
            var writeTask = write.ExecuteNonQueryAsync();
            await firstTransaction.CommitAsync(CancellationToken.None);

            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await writeTask;
                await secondTransaction.CommitAsync(CancellationToken.None);
            });

            // 40001 serialization_failure, or 40P01 deadlock_detected.
            Assert.StartsWith("40", exception.SqlState, StringComparison.Ordinal);
        }
    }
}
