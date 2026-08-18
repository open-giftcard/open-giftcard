using GiftCardPlatform.Modules.Distribution.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Distribution.Infrastructure;

internal sealed class DistributionInvitationConfiguration :
    IEntityTypeConfiguration<DistributionInvitation>
{
    public void Configure(EntityTypeBuilder<DistributionInvitation> builder)
    {
        builder.ToTable("invitations", DistributionDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_distribution_invitations_contact_type",
                "\"contact_type\" is null or \"contact_type\" in ('Email', 'Phone')");
            table.HasCheckConstraint(
                "ck_distribution_invitations_kind",
                """
                ("kind" = 'Directed'
                    AND "contact_type" IS NOT NULL
                    AND "recipient_contact" IS NOT NULL
                    AND "masked_recipient_contact" IS NOT NULL
                    AND "pin_hash" IS NULL
                    AND "distributed_by_membership_id" IS NOT NULL
                    AND "distributed_by_partner_client_id" IS NULL)
                OR
                ("kind" = 'OrphanPin'
                    AND "contact_type" IS NULL
                    AND "recipient_contact" IS NULL
                    AND "masked_recipient_contact" IS NULL
                    AND "pin_hash" IS NOT NULL
                    AND "distributed_by_membership_id" IS NULL
                    AND "distributed_by_partner_client_id" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "ck_distribution_invitations_state",
                """
                ("state" = 'Pending'
                    AND "claimed_by_user_id" IS NULL
                    AND "claimed_at_utc" IS NULL
                    AND "claim_idempotency_key" IS NULL
                    AND "identity_was_created_on_claim" IS NULL)
                OR
                ("state" = 'Claimed'
                    AND "claimed_by_user_id" IS NOT NULL
                    AND "claimed_at_utc" IS NOT NULL
                    AND "claim_idempotency_key" IS NOT NULL
                    AND "identity_was_created_on_claim" IS NOT NULL)
                OR
                ("state" IN ('Locked', 'Expired', 'Cancelled')
                    AND "claimed_by_user_id" IS NULL
                    AND "claimed_at_utc" IS NULL
                    AND "claim_idempotency_key" IS NULL
                    AND "identity_was_created_on_claim" IS NULL)
                """);
            table.HasCheckConstraint(
                "ck_distribution_invitations_expiry",
                "\"claim_expires_at_utc\" > \"distributed_at_utc\"");
            table.HasCheckConstraint(
                "ck_distribution_invitations_failed_attempts",
                "\"failed_claim_attempts\" >= 0");
        });

        builder.HasKey(invitation => invitation.Id);
        builder.Property(invitation => invitation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(invitation => invitation.FundingOrganizationId)
            .HasColumnName("funding_organization_id")
            .IsRequired();
        builder.Property(invitation => invitation.IssuingOrganizationId)
            .HasColumnName("issuing_organization_id")
            .IsRequired();
        builder.Property(invitation => invitation.GiftCardId)
            .HasColumnName("gift_card_id")
            .IsRequired();
        builder.Property(invitation => invitation.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(invitation => invitation.ContactType)
            .HasColumnName("contact_type")
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(invitation => invitation.RecipientContact)
            .HasColumnName("recipient_contact")
            .HasMaxLength(DistributionIntent.ContactMaxLength);
        builder.Property(invitation => invitation.MaskedRecipientContact)
            .HasColumnName("masked_recipient_contact")
            .HasMaxLength(DistributionIntent.ContactMaxLength);
        builder.Property(invitation => invitation.ClaimSecretHash)
            .HasColumnName("claim_secret_hash")
            .HasMaxLength(ClaimTokenCodec.HashHexLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(invitation => invitation.PinHash)
            .HasColumnName("pin_hash")
            .HasMaxLength(EpinCredentialCodec.PinHashHexLength)
            .IsFixedLength();
        builder.Property(invitation => invitation.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(invitation => invitation.ClaimExpiresAtUtc)
            .HasColumnName("claim_expires_at_utc")
            .IsRequired();
        builder.Property(invitation => invitation.FailedClaimAttempts)
            .HasColumnName("failed_claim_attempts")
            .IsRequired();
        builder.Property(invitation => invitation.BusinessReference)
            .HasColumnName("business_reference")
            .HasMaxLength(DistributionIntent.BusinessReferenceMaxLength)
            .IsRequired();
        builder.Property(invitation => invitation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(DistributionIntent.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(invitation => invitation.DistributedByUserId)
            .HasColumnName("distributed_by_user_id")
            .IsRequired();
        builder.Property(invitation => invitation.DistributedByMembershipId)
            .HasColumnName("distributed_by_membership_id");
        builder.Property(invitation => invitation.DistributedByPartnerClientId)
            .HasColumnName("distributed_by_partner_client_id");
        builder.Property(invitation => invitation.DistributedAtUtc)
            .HasColumnName("distributed_at_utc")
            .IsRequired();
        builder.Property(invitation => invitation.ClaimedByUserId)
            .HasColumnName("claimed_by_user_id");
        builder.Property(invitation => invitation.ClaimedAtUtc)
            .HasColumnName("claimed_at_utc");
        builder.Property(invitation => invitation.ClaimIdempotencyKey)
            .HasColumnName("claim_idempotency_key")
            .HasMaxLength(DistributionIntent.IdempotencyKeyMaxLength);
        builder.Property(invitation => invitation.IdentityWasCreatedOnClaim)
            .HasColumnName("identity_was_created_on_claim");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(invitation => new
        {
            invitation.FundingOrganizationId,
            invitation.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName("ux_distribution_tenant_idempotency");
        builder.HasIndex(invitation => invitation.GiftCardId)
            .IsUnique()
            .HasFilter("\"state\" = 'Pending'")
            .HasDatabaseName("ux_distribution_gift_card");
        builder.HasIndex(invitation => new
        {
            invitation.IssuingOrganizationId,
            invitation.DistributedAtUtc,
            invitation.Id,
        })
            .HasDatabaseName("ix_distribution_organization_history");
        builder.HasIndex(invitation => new
        {
            invitation.ClaimedByUserId,
            invitation.ClaimedAtUtc,
        })
            .HasFilter("\"claimed_by_user_id\" IS NOT NULL")
            .HasDatabaseName("ix_distribution_identity_history");
        builder.HasIndex(invitation => invitation.DistributedByPartnerClientId)
            .HasFilter("\"distributed_by_partner_client_id\" IS NOT NULL")
            .HasDatabaseName("ix_distribution_invitations_partner_client");
    }
}

internal sealed class DistributionEventConfiguration :
    IEntityTypeConfiguration<DistributionEvent>
{
    public void Configure(EntityTypeBuilder<DistributionEvent> builder)
    {
        builder.ToTable("events", DistributionDbContext.Schema);
        builder.HasKey(distributionEvent => distributionEvent.Id);
        builder.Property(distributionEvent => distributionEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(distributionEvent => distributionEvent.FundingOrganizationId)
            .HasColumnName("funding_organization_id")
            .IsRequired();
        builder.Property(distributionEvent => distributionEvent.InvitationId)
            .HasColumnName("invitation_id")
            .IsRequired();
        builder.Property(distributionEvent => distributionEvent.GiftCardId)
            .HasColumnName("gift_card_id")
            .IsRequired();
        builder.Property(distributionEvent => distributionEvent.Type)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(distributionEvent => distributionEvent.ActorUserId)
            .HasColumnName("actor_user_id");
        builder.Property(distributionEvent => distributionEvent.ActorMembershipId)
            .HasColumnName("actor_membership_id");
        builder.Property(distributionEvent => distributionEvent.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .IsRequired();

        builder.HasIndex(distributionEvent => new
        {
            distributionEvent.InvitationId,
            distributionEvent.OccurredAtUtc,
            distributionEvent.Id,
        })
            .HasDatabaseName("ix_distribution_events_invitation_history");
        builder.HasIndex(distributionEvent => new
        {
            distributionEvent.GiftCardId,
            distributionEvent.OccurredAtUtc,
            distributionEvent.Id,
        })
            .HasDatabaseName("ix_distribution_events_card_history");

        builder.HasOne<DistributionInvitation>()
            .WithMany()
            .HasForeignKey(distributionEvent => distributionEvent.InvitationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BulkGiftCardBatchConfiguration :
    IEntityTypeConfiguration<BulkGiftCardBatch>
{
    public void Configure(EntityTypeBuilder<BulkGiftCardBatch> builder)
    {
        builder.ToTable("bulk_batches", DistributionDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_distribution_bulk_batches_state",
                """
                ("state" IN ('Pending', 'Processing') AND "completed_at_utc" IS NULL)
                OR
                ("state" = 'Completed' AND "completed_at_utc" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "ck_distribution_bulk_batches_total_items",
                $"\"total_items\" between 1 and {BulkGiftCardBatchIntent.MaximumAsyncItems}");
            table.HasCheckConstraint(
                "ck_distribution_bulk_batches_counts",
                """
                "succeeded_items" >= 0
                AND "failed_items" >= 0
                AND "succeeded_items" + "failed_items" <= "total_items"
                AND (
                    "state" <> 'Completed'
                    OR "succeeded_items" + "failed_items" = "total_items"
                )
                """);
            table.HasCheckConstraint(
                "ck_distribution_bulk_batches_completion",
                """
                "completed_at_utc" IS NULL
                OR "completed_at_utc" >= "created_at_utc"
                """);
        });

        builder.HasKey(batch => batch.Id);
        builder.Property(batch => batch.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(batch => batch.FundingOrganizationId)
            .HasColumnName("funding_organization_id")
            .IsRequired();
        builder.Property(batch => batch.IssuingOrganizationId)
            .HasColumnName("issuing_organization_id")
            .IsRequired();
        builder.Property(batch => batch.BatchReference)
            .HasColumnName("batch_reference")
            .HasMaxLength(BulkGiftCardBatchIntent.BatchReferenceMaxLength)
            .IsRequired();
        builder.Property(batch => batch.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(DistributionIntent.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(batch => batch.IntentHash)
            .HasColumnName("intent_hash")
            .HasMaxLength(BulkGiftCardBatchIntent.IntentHashLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(batch => batch.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(batch => batch.TotalItems)
            .HasColumnName("total_items")
            .IsRequired();
        builder.Property(batch => batch.SucceededItems)
            .HasColumnName("succeeded_items")
            .IsRequired();
        builder.Property(batch => batch.FailedItems)
            .HasColumnName("failed_items")
            .IsRequired();
        builder.Property(batch => batch.CreatedByUserId)
            .HasColumnName("created_by_user_id")
            .IsRequired();
        builder.Property(batch => batch.CreatedByMembershipId)
            .HasColumnName("created_by_membership_id")
            .IsRequired();
        builder.Property(batch => batch.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();
        builder.Property(batch => batch.CompletedAtUtc)
            .HasColumnName("completed_at_utc");
        builder.Property(batch => batch.RetryOfBatchId)
            .HasColumnName("retry_of_batch_id");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(batch => new
        {
            batch.FundingOrganizationId,
            batch.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName("ux_distribution_bulk_batch_tenant_idempotency");
        builder.HasIndex(batch => new
        {
            batch.IssuingOrganizationId,
            batch.CreatedAtUtc,
            batch.Id,
        })
            .HasDatabaseName("ix_distribution_bulk_batch_history");
        builder.HasIndex(batch => batch.RetryOfBatchId)
            .IsUnique()
            .HasFilter("\"retry_of_batch_id\" IS NOT NULL")
            .HasDatabaseName("ux_distribution_bulk_batch_retry_parent");

        builder.HasOne<BulkGiftCardBatch>()
            .WithMany()
            .HasForeignKey(batch => batch.RetryOfBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(batch => batch.Items)
            .WithOne()
            .HasForeignKey(item => item.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(batch => batch.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class BulkGiftCardBatchItemConfiguration :
    IEntityTypeConfiguration<BulkGiftCardBatchItem>
{
    public void Configure(EntityTypeBuilder<BulkGiftCardBatchItem> builder)
    {
        builder.ToTable("bulk_items", DistributionDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_distribution_bulk_items_position",
                "\"position\" > 0");
            table.HasCheckConstraint(
                "ck_distribution_bulk_items_amount",
                "\"amount\" > 0");
            table.HasCheckConstraint(
                "ck_distribution_bulk_items_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint(
                "ck_distribution_bulk_items_validity",
                "\"expires_at_utc\" > \"valid_from_utc\"");
            table.HasCheckConstraint(
                "ck_distribution_bulk_items_outcome",
                """
                (
                    "state" = 'Pending'
                    AND "gift_card_id" IS NULL
                    AND "gift_card_public_reference" IS NULL
                    AND "invitation_id" IS NULL
                    AND "gift_card_state" IS NULL
                    AND "invitation_state" IS NULL
                    AND "distributed_at_utc" IS NULL
                    AND "failure_code" IS NULL
                    AND "failure_message" IS NULL
                    AND "settled_at_utc" IS NULL
                )
                OR
                (
                    "state" = 'Succeeded'
                    AND "gift_card_id" IS NOT NULL
                    AND "gift_card_public_reference" IS NOT NULL
                    AND "invitation_id" IS NOT NULL
                    AND "gift_card_state" = 'AwaitingClaim'
                    AND "invitation_state" = 'Pending'
                    AND "distributed_at_utc" IS NOT NULL
                    AND "failure_code" IS NULL
                    AND "failure_message" IS NULL
                    AND "settled_at_utc" IS NOT NULL
                )
                OR
                (
                    "state" = 'Failed'
                    AND "gift_card_id" IS NULL
                    AND "gift_card_public_reference" IS NULL
                    AND "invitation_id" IS NULL
                    AND "gift_card_state" IS NULL
                    AND "invitation_state" IS NULL
                    AND "distributed_at_utc" IS NULL
                    AND "failure_code" IS NOT NULL
                    AND "failure_message" IS NOT NULL
                    AND "settled_at_utc" IS NOT NULL
                )
                """);
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(item => item.BatchId)
            .HasColumnName("batch_id")
            .IsRequired();
        builder.Property(item => item.FundingOrganizationId)
            .HasColumnName("funding_organization_id")
            .IsRequired();
        builder.Property(item => item.IssuingOrganizationId)
            .HasColumnName("issuing_organization_id")
            .IsRequired();
        builder.Property(item => item.Position)
            .HasColumnName("position")
            .IsRequired();
        builder.Property(item => item.ItemReference)
            .HasColumnName("item_reference")
            .HasMaxLength(BulkGiftCardBatchIntent.ItemReferenceMaxLength)
            .IsRequired();
        builder.Property(item => item.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(item => item.GiftCardId)
            .HasColumnName("gift_card_id");
        builder.Property(item => item.GiftCardPublicReference)
            .HasColumnName("gift_card_public_reference")
            .HasMaxLength(32);
        builder.Property(item => item.InvitationId)
            .HasColumnName("invitation_id");
        builder.Property(item => item.ContactType)
            .HasColumnName("contact_type")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(item => item.MaskedRecipientContact)
            .HasColumnName("masked_recipient_contact")
            .HasMaxLength(DistributionIntent.ContactMaxLength)
            .IsRequired();
        builder.Property(item => item.RecipientContact)
            .HasColumnName("recipient_contact")
            .HasMaxLength(DistributionIntent.ContactMaxLength)
            .IsRequired();
        builder.Property(item => item.Amount)
            .HasColumnName("amount")
            .HasPrecision(20, 4)
            .IsRequired();
        builder.Property(item => item.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsFixedLength()
            .IsRequired();
        builder.Property(item => item.ValidFromUtc)
            .HasColumnName("valid_from_utc")
            .IsRequired();
        builder.Property(item => item.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .IsRequired();
        builder.Property(item => item.IsTransferable)
            .HasColumnName("is_transferable")
            .IsRequired();
        builder.Property(item => item.IsDivisible)
            .HasColumnName("is_divisible")
            .IsRequired();
        builder.Property(item => item.IssuanceIdempotencyKey)
            .HasColumnName("issuance_idempotency_key")
            .HasMaxLength(DistributionIntent.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(item => item.DistributionIdempotencyKey)
            .HasColumnName("distribution_idempotency_key")
            .HasMaxLength(DistributionIntent.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(item => item.GiftCardState)
            .HasColumnName("gift_card_state")
            .HasMaxLength(32);
        builder.Property(item => item.InvitationState)
            .HasColumnName("invitation_state")
            .HasMaxLength(16);
        builder.Property(item => item.DistributedAtUtc)
            .HasColumnName("distributed_at_utc");
        builder.Property(item => item.FailureCode)
            .HasColumnName("failure_code")
            .HasMaxLength(160);
        builder.Property(item => item.FailureMessage)
            .HasColumnName("failure_message")
            .HasMaxLength(500);
        builder.Property(item => item.SettledAtUtc)
            .HasColumnName("settled_at_utc");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(item => new { item.BatchId, item.Position })
            .IsUnique()
            .HasDatabaseName("ux_distribution_bulk_item_position");
        builder.HasIndex(item => new { item.BatchId, item.ItemReference })
            .IsUnique()
            .HasDatabaseName("ux_distribution_bulk_item_reference");
        builder.HasIndex(item => item.GiftCardId)
            .IsUnique()
            .HasFilter("\"gift_card_id\" IS NOT NULL")
            .HasDatabaseName("ux_distribution_bulk_item_card");
        builder.HasIndex(item => item.InvitationId)
            .IsUnique()
            .HasFilter("\"invitation_id\" IS NOT NULL")
            .HasDatabaseName("ux_distribution_bulk_item_invitation");
    }
}
