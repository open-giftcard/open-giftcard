using GiftCardPlatform.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Notifications.Infrastructure;

internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public const string Schema = "notifications";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<OutboxMessage> Messages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());

        // Deliberately no tenant query filter. The dispatcher is trusted-system
        // work that must see every organization's queue, and a message is never
        // read through a tenant-scoped caller path. Access is restricted by the
        // system-context check in the dispatcher and by RLS, not by a filter.
    }
}
