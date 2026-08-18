using GiftCardPlatform.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Notifications.Infrastructure;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages", NotificationsDbContext.Schema, table =>
        {
            // A pending message must still be deliverable, and a settled one
            // must not be. This is what keeps "the credential is gone" and "the
            // message is terminal" from ever disagreeing.
            table.HasCheckConstraint(
                "ck_outbox_messages_settlement",
                "(\"state\" = 'Pending') = (\"settled_at_utc\" IS NULL)");

            // The credential-bearing columns exist only while pending. Enforced
            // here as well as in the domain, so a defect in a future code path
            // cannot leave a live activation link behind a delivered row.
            table.HasCheckConstraint(
                "ck_outbox_messages_payload_lifetime",
                "(\"state\" = 'Pending') = (\"protected_body\" IS NOT NULL)");

            table.HasCheckConstraint(
                "ck_outbox_messages_attempts",
                "\"attempt_count\" >= 0");
        });

        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(message => message.Kind)
            .HasColumnName("kind").HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(message => message.Channel)
            .HasColumnName("channel").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(message => message.State)
            .HasColumnName("state").HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(message => message.ProtectedRecipient)
            .HasColumnName("protected_recipient").IsRequired();
        builder.Property(message => message.MaskedRecipient)
            .HasColumnName("masked_recipient")
            .HasMaxLength(OutboxMessage.RecipientMaxLength).IsRequired();
        builder.Property(message => message.Subject)
            .HasColumnName("subject").HasMaxLength(OutboxMessage.SubjectMaxLength).IsRequired();
        builder.Property(message => message.ProtectedBody).HasColumnName("protected_body");

        builder.Property(message => message.OrganizationId).HasColumnName("organization_id");
        builder.Property(message => message.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(message => message.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(OutboxMessage.IdempotencyKeyMaxLength).IsRequired();

        builder.Property(message => message.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(message => message.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(message => message.NextAttemptAtUtc)
            .HasColumnName("next_attempt_at_utc").IsRequired();
        builder.Property(message => message.SettledAtUtc).HasColumnName("settled_at_utc");
        builder.Property(message => message.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(message => message.LastFailureCode)
            .HasColumnName("last_failure_code").HasMaxLength(OutboxMessage.FailureCodeMaxLength);

        builder.Property(message => message.Version)
            .HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsRowVersion();

        // The real guarantee that one business operation queues one message.
        builder.HasIndex(message => message.IdempotencyKey)
            .HasDatabaseName("ux_outbox_messages_idempotency_key").IsUnique();

        // The dispatcher's only hot query.
        builder.HasIndex(message => new { message.State, message.NextAttemptAtUtc })
            .HasDatabaseName("ix_outbox_messages_due");
    }
}
