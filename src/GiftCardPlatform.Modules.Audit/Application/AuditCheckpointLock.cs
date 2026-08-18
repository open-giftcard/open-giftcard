using GiftCardPlatform.Modules.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Audit.Application;

internal static class AuditCheckpointLock
{
    // Stable, project-owned PostgreSQL advisory-lock namespace for ADR-013.
    private const string WriterSql =
        "select pg_advisory_xact_lock_shared(4697588874431775817)"; // Arbitrary fixed lock id; kept stable so every instance takes the same lock.
    private const string SealerSql =
        "select pg_advisory_xact_lock(4697588874431775817)";

    public static Task AcquireWriterAsync(
        AuditDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(WriterSql, cancellationToken);

    public static Task AcquireSealerAsync(
        AuditDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(SealerSql, cancellationToken);
}
