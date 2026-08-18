using GiftCardPlatform.Modules.Sharing.Contracts;
using GiftCardPlatform.Modules.Sharing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Sharing.Infrastructure;

internal sealed class GiftCardShareConfiguration : IEntityTypeConfiguration<GiftCardShare>
{
    public void Configure(EntityTypeBuilder<GiftCardShare> builder)
    {
        builder.ToTable("shares", SharingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_sharing_amount", "\"amount\" > 0");
            table.HasCheckConstraint("ck_sharing_currency", "\"currency\" ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_sharing_failed_attempts", "\"failed_pin_attempts\" >= 0");
            table.HasCheckConstraint("ck_sharing_expiry", "\"expires_at_utc\" > \"created_at_utc\"");
            table.HasCheckConstraint(
                "ck_sharing_kind",
                """
                ("kind" = 'ProtectedLink'
                    AND "pin_hash" IS NOT NULL
                    AND "recipient_contact_type" IS NULL
                    AND "recipient_contact" IS NULL
                    AND "masked_recipient_contact" IS NULL)
                OR
                ("kind" = 'DirectInvitation'
                    AND "pin_hash" IS NULL
                    AND "recipient_contact_type" IN ('Email', 'Phone')
                    AND "recipient_contact" IS NOT NULL
                    AND "masked_recipient_contact" IS NOT NULL
                    AND "failed_pin_attempts" = 0)
                """);
            table.HasCheckConstraint(
                "ck_sharing_state",
                """
                ("state" = 'Pending'
                    AND "claimed_by_user_id" IS NULL
                    AND "child_gift_card_id" IS NULL
                    AND "ledger_transaction_id" IS NULL
                    AND "claim_idempotency_key" IS NULL
                    AND "identity_was_created_on_claim" IS NULL
                    AND "claimed_at_utc" IS NULL
                    AND "closed_at_utc" IS NULL)
                OR
                ("state" = 'Claiming'
                    AND "claimed_by_user_id" IS NOT NULL
                    AND "child_gift_card_id" IS NOT NULL
                    AND "ledger_transaction_id" IS NOT NULL
                    AND "claim_idempotency_key" IS NOT NULL
                    AND "identity_was_created_on_claim" IS NULL
                    AND "claimed_at_utc" IS NULL
                    AND "closed_at_utc" IS NULL)
                OR
                ("state" = 'Claimed'
                    AND "claimed_by_user_id" IS NOT NULL
                    AND "child_gift_card_id" IS NOT NULL
                    AND "ledger_transaction_id" IS NOT NULL
                    AND "claim_idempotency_key" IS NOT NULL
                    AND (("kind" = 'ProtectedLink' AND "identity_was_created_on_claim" IS NULL)
                         OR ("kind" = 'DirectInvitation' AND "identity_was_created_on_claim" IS NOT NULL))
                    AND "claimed_at_utc" IS NOT NULL
                    AND "closed_at_utc" IS NOT NULL)
                OR
                ("state" IN ('Cancelled', 'Expired', 'Locked')
                    AND "claimed_by_user_id" IS NULL
                    AND "child_gift_card_id" IS NULL
                    AND "ledger_transaction_id" IS NULL
                    AND "claim_idempotency_key" IS NULL
                    AND "identity_was_created_on_claim" IS NULL
                    AND "claimed_at_utc" IS NULL
                    AND "closed_at_utc" IS NOT NULL)
                """);
        });

        builder.HasKey(share => share.Id);
        builder.Property(share => share.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(share => share.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(share => share.SourceGiftCardId).HasColumnName("source_gift_card_id").IsRequired();
        builder.Property(share => share.FundingOrganizationId).HasColumnName("funding_organization_id").IsRequired();
        builder.Property(share => share.SenderUserId).HasColumnName("sender_user_id").IsRequired();
        builder.Property(share => share.ClaimedByUserId).HasColumnName("claimed_by_user_id");
        builder.Property(share => share.ChildGiftCardId).HasColumnName("child_gift_card_id");
        builder.Property(share => share.LedgerTransactionId).HasColumnName("ledger_transaction_id");
        builder.Property(share => share.Amount).HasColumnName("amount").HasPrecision(19, 4).IsRequired();
        builder.Property(share => share.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(share => share.ClaimSecretHash).HasColumnName("claim_secret_hash").HasMaxLength(ShareTokenCodec.HashHexLength).IsFixedLength().IsRequired();
        builder.Property(share => share.PinHash).HasColumnName("pin_hash").HasMaxLength(SharePinCodec.PersistedLength);
        builder.Property(share => share.RecipientContactType).HasColumnName("recipient_contact_type").HasConversion<string>().HasMaxLength(16);
        builder.Property(share => share.RecipientContact).HasColumnName("recipient_contact").HasMaxLength(320);
        builder.Property(share => share.MaskedRecipientContact).HasColumnName("masked_recipient_contact").HasMaxLength(320);
        builder.Property(share => share.IdentityWasCreatedOnClaim).HasColumnName("identity_was_created_on_claim");
        builder.Property(share => share.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(share => share.FailedPinAttempts).HasColumnName("failed_pin_attempts").IsRequired();
        builder.Property(share => share.CreateIdempotencyKey).HasColumnName("create_idempotency_key").HasMaxLength(GiftCardShare.IdempotencyKeyMaxLength).IsRequired();
        builder.Property(share => share.ClaimIdempotencyKey).HasColumnName("claim_idempotency_key").HasMaxLength(GiftCardShare.IdempotencyKeyMaxLength);
        builder.Property(share => share.CancelIdempotencyKey).HasColumnName("cancel_idempotency_key").HasMaxLength(GiftCardShare.IdempotencyKeyMaxLength);
        builder.Property(share => share.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(share => share.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(share => share.ClaimedAtUtc).HasColumnName("claimed_at_utc");
        builder.Property(share => share.ClosedAtUtc).HasColumnName("closed_at_utc");
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(share => new { share.SenderUserId, share.CreateIdempotencyKey })
            .IsUnique().HasDatabaseName("ux_sharing_sender_idempotency");
        builder.HasIndex(share => new { share.SourceGiftCardId, share.State })
            .HasDatabaseName("ix_sharing_source_active");
        builder.HasIndex(share => new { share.SenderUserId, share.CreatedAtUtc, share.Id })
            .HasDatabaseName("ix_sharing_sender_history");
        builder.HasIndex(share => new { share.ClaimedByUserId, share.ClaimedAtUtc, share.Id })
            .HasFilter("\"claimed_by_user_id\" IS NOT NULL")
            .HasDatabaseName("ix_sharing_recipient_history");
        builder.HasIndex(share => new { share.State, share.ExpiresAtUtc, share.Id })
            .HasDatabaseName("ix_sharing_expiration");
        builder.HasIndex(share => share.ChildGiftCardId)
            .IsUnique().HasFilter("\"child_gift_card_id\" IS NOT NULL")
            .HasDatabaseName("ux_sharing_child_card");
        builder.HasIndex(share => share.LedgerTransactionId)
            .IsUnique().HasFilter("\"ledger_transaction_id\" IS NOT NULL")
            .HasDatabaseName("ux_sharing_ledger_transaction");
    }
}

internal sealed class GiftCardShareEventConfiguration : IEntityTypeConfiguration<GiftCardShareEvent>
{
    public void Configure(EntityTypeBuilder<GiftCardShareEvent> builder)
    {
        builder.ToTable("events", SharingDbContext.Schema);
        builder.HasKey(shareEvent => shareEvent.Id);
        builder.Property(shareEvent => shareEvent.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(shareEvent => shareEvent.ShareId).HasColumnName("share_id").IsRequired();
        builder.Property(shareEvent => shareEvent.FundingOrganizationId).HasColumnName("funding_organization_id").IsRequired();
        builder.Property(shareEvent => shareEvent.Type).HasColumnName("event_type").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(shareEvent => shareEvent.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(shareEvent => shareEvent.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.HasIndex(shareEvent => new { shareEvent.ShareId, shareEvent.OccurredAtUtc, shareEvent.Id })
            .HasDatabaseName("ix_sharing_events_history");
        builder.HasOne<GiftCardShare>().WithMany().HasForeignKey(shareEvent => shareEvent.ShareId).OnDelete(DeleteBehavior.Restrict);
    }
}
