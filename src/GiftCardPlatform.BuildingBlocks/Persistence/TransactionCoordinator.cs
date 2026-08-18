using System.Data;
using GiftCardPlatform.BuildingBlocks.Execution;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.BuildingBlocks.Persistence;

/// <summary>
/// An explicit, in-progress transaction spanning one or more module DbContexts
/// (ADR-011). Not an ambient <c>TransactionScope</c>: the boundary is passed
/// explicitly so its behaviour is visible and testable.
///
/// A handle may be the outermost scope, which owns the real database
/// transaction, or a nested scope that joins it. Only the outermost scope
/// commits (ADR-026).
/// </summary>
public interface IModuleTransaction : IAsyncDisposable
{
    NpgsqlTransaction Transaction { get; }

    /// <summary>True when this handle owns the underlying database transaction.</summary>
    bool IsOutermost { get; }

    /// <summary>Enlists a module DbContext so its writes join this transaction.</summary>
    Task EnlistAsync(DbContext dbContext, CancellationToken cancellationToken);

    /// <summary>
    /// Completes this scope. On the outermost scope this commits; on a nested
    /// scope it only records that the scope succeeded. A nested scope that is
    /// disposed without completing dooms the whole transaction.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Begins transactions that share one physical Npgsql connection across module
/// DbContexts, so a business change and its audit record commit atomically.
///
/// Calls nest: a service that begins a transaction while one is already in
/// progress joins it rather than failing, which is what lets one business
/// operation span modules that each own a transaction boundary (ADR-026).
/// </summary>
public interface ITransactionCoordinator
{
    /// <summary>The transaction currently in progress in this scope, if any.</summary>
    IModuleTransaction? Current { get; }

    /// <summary>Begins or joins a transaction at the default isolation level.</summary>
    Task<IModuleTransaction> BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Begins or joins a transaction at the requested isolation level. Financial
    /// operations that must not permit lost updates ask for
    /// <see cref="IsolationLevel.Serializable"/> here.
    ///
    /// PostgreSQL cannot change isolation once a transaction has started, so
    /// joining an in-progress transaction that is weaker than requested throws
    /// rather than silently providing a weaker guarantee than the caller asked
    /// for.
    /// </summary>
    Task<IModuleTransaction> BeginAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);
}

// CA1001: the in-progress transaction is owned by the caller, which disposes it
// via `await using`. The coordinator only tracks it so other modules can enlist.
#pragma warning disable CA1001
public sealed class TransactionCoordinator(
    ScopedDatabaseConnection connection,
    ISessionContextWriter sessionContextWriter,
    IExecutionContext executionContext) : ITransactionCoordinator
#pragma warning restore CA1001
{
    /// <summary>
    /// PostgreSQL's default. Sufficient for the administrative operations built
    /// so far; value-changing operations must request a stronger level.
    /// </summary>
    public const IsolationLevel DefaultIsolationLevel = IsolationLevel.ReadCommitted;

    private RootModuleTransaction? _root;

    public IModuleTransaction? Current => _root;

    public Task<IModuleTransaction> BeginAsync(CancellationToken cancellationToken) =>
        BeginAsync(DefaultIsolationLevel, cancellationToken);

    public async Task<IModuleTransaction> BeginAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (_root is not null)
        {
            _root.EnsureIsolationSatisfies(isolationLevel);
            return _root.CreateNested();
        }

        var open = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await open
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        // Establish session context before any module SQL runs, so it covers
        // reads as well as writes.
        await sessionContextWriter
            .WriteAsync(open, transaction, executionContext, cancellationToken)
            .ConfigureAwait(false);

        // Cleared by reference, never unconditionally: a scope that commits then
        // disposes must not null out a *different* transaction that was started
        // in between.
        _root = new RootModuleTransaction(
            transaction,
            isolationLevel,
            completed =>
            {
                if (ReferenceEquals(_root, completed))
                {
                    _root = null;
                }
            });

        return _root;
    }

    private sealed class RootModuleTransaction(
        NpgsqlTransaction transaction,
        IsolationLevel isolationLevel,
        Action<RootModuleTransaction> onCompleted) : IModuleTransaction
    {
        private readonly HashSet<DbContext> _enlisted = [];
        private bool _committed;
        private bool _rollbackOnly;

        public NpgsqlTransaction Transaction => transaction;

        public bool IsOutermost => true;

        public NestedModuleTransaction CreateNested() => new(this);

        /// <summary>
        /// Rejects joining when the in-progress transaction is weaker than the
        /// caller requires. Isolation cannot be raised after a transaction has
        /// begun, so the only honest options are to fail or to mislead.
        /// </summary>
        public void EnsureIsolationSatisfies(IsolationLevel requested)
        {
            if (requested == IsolationLevel.Unspecified || requested <= isolationLevel)
            {
                return;
            }

            throw new InvalidOperationException(
                $"A transaction is already in progress at {isolationLevel}, which does not satisfy the " +
                $"requested {requested}. PostgreSQL cannot raise isolation after a transaction has started: " +
                "begin the outermost transaction at the stronger level instead.");
        }

        public async Task EnlistAsync(DbContext dbContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(dbContext);

            if (!_enlisted.Add(dbContext))
            {
                return;
            }

            await dbContext.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Records that a nested scope ended without completing.</summary>
        public void MarkRollbackOnly() => _rollbackOnly = true;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_rollbackOnly)
            {
                // A nested scope failed. Committing here would persist a partial
                // unit of work, so refuse; disposal rolls the whole thing back.
                throw new InvalidOperationException(
                    "The transaction cannot be committed because a nested scope was abandoned without completing.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;

            // A committed transaction is finished, so it must stop being the
            // ambient one immediately. Waiting for disposal would let a later
            // call join a transaction that can no longer accept work.
            onCompleted(this);
        }

        public async ValueTask DisposeAsync()
        {
            // Any path that leaves without committing — including an exception
            // thrown while writing the audit record — rolls the whole unit back.
            if (!_committed)
            {
                try
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Transaction already completed or connection already closed.
                }
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            onCompleted(this);
        }
    }

    /// <summary>
    /// A scope that joined an in-progress transaction. It can enlist contexts and
    /// signal success, but it never commits or rolls back the shared transaction
    /// — that stays with the outermost scope.
    /// </summary>
    private sealed class NestedModuleTransaction(RootModuleTransaction root) : IModuleTransaction
    {
        private bool _completed;

        public NpgsqlTransaction Transaction => root.Transaction;

        public bool IsOutermost => false;

        public Task EnlistAsync(DbContext dbContext, CancellationToken cancellationToken) =>
            root.EnlistAsync(dbContext, cancellationToken);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            _completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                // Leaving a nested scope without completing means the inner
                // operation failed, so the outer one must not be allowed to
                // commit a half-finished unit of work.
                root.MarkRollbackOnly();
            }

            return ValueTask.CompletedTask;
        }
    }
}
