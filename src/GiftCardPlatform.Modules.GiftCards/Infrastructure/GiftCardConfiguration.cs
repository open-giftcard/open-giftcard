using GiftCardPlatform.Modules.GiftCards.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.GiftCards.Infrastructure;

internal sealed class GiftCardConfiguration : IEntityTypeConfiguration<GiftCard>
{
    public void Configure(EntityTypeBuilder<GiftCard> builder)
    {
        builder.ToTable("gift_cards", GiftCardsDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_gift_cards_amount",
                "\"initial_value\" > 0");
            table.HasCheckConstraint(
                "ck_gift_cards_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint(
                "ck_gift_cards_validity",
                "\"expires_at_utc\" > \"valid_from_utc\"");
            // A card is minted either by a person acting through a membership
            // or by a partner API client, never both and never neither, so a
            // minted card always has a traceable issuer (ADR-053).
            table.HasCheckConstraint(
                "ck_gift_cards_issuer_attribution",
                """
                ("issued_by_membership_id" IS NOT NULL
                    AND "issued_by_partner_client_id" IS NULL)
                OR
                ("issued_by_membership_id" IS NULL
                    AND "issued_by_partner_client_id" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "ck_gift_cards_ownership",
                """
                ("ownership_state" = 'OrganizationInventory'
                    AND "owner_organization_id" IS NOT NULL
                    AND "owner_user_id" IS NULL)
                OR
                ("ownership_state" = 'AwaitingClaim'
                    AND "owner_organization_id" IS NULL
                    AND "owner_user_id" IS NULL)
                OR
                ("ownership_state" = 'IdentityOwned'
                    AND "owner_organization_id" IS NULL
                    AND "owner_user_id" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "ck_gift_cards_provenance",
                """
                ("generation" = 0
                    AND "source_gift_card_id" IS NULL
                    AND "root_gift_card_id" = "id")
                OR
                ("generation" > 0
                    AND "source_gift_card_id" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "ck_gift_cards_distribution_state",
                """
                ("ownership_state" = 'OrganizationInventory'
                    AND "lifecycle_state" IN (
                        'Active', 'Suspended', 'Cancelled', 'Expired')
                    AND "distribution_invitation_id" IS NULL
                    AND "distributed_at_utc" IS NULL
                    AND "claimed_at_utc" IS NULL)
                OR
                ("ownership_state" = 'AwaitingClaim'
                    AND "lifecycle_state" IN (
                        'AwaitingClaim', 'Suspended', 'Cancelled', 'Expired')
                    AND "distribution_invitation_id" IS NOT NULL
                    AND "distributed_at_utc" IS NOT NULL
                    AND "claimed_at_utc" IS NULL)
                OR
                ("ownership_state" = 'IdentityOwned'
                    AND "lifecycle_state" IN (
                        'Active', 'Suspended', 'Cancelled', 'Expired')
                    AND "claimed_at_utc" IS NOT NULL
                    AND (
                        ("generation" = 0
                            AND "distribution_invitation_id" IS NOT NULL
                            AND "distributed_at_utc" IS NOT NULL)
                        OR
                        ("generation" > 0
                            AND "source_gift_card_id" IS NOT NULL
                            AND "distribution_invitation_id" IS NULL
                            AND "distributed_at_utc" IS NULL)))
                """);
        });

        builder.HasKey(card => card.Id);
        builder.Property(card => card.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(card => card.PublicReference)
            .HasColumnName("public_reference")
            .HasMaxLength(GiftCard.PublicReferenceMaxLength)
            .IsRequired();
        builder.Property(card => card.FundingOrganizationId)
            .HasColumnName("funding_organization_id")
            .IsRequired();
        builder.Property(card => card.IssuingOrganizationId)
            .HasColumnName("issuing_organization_id")
            .IsRequired();
        builder.Property(card => card.OwnerOrganizationId)
            .HasColumnName("owner_organization_id");
        builder.Property(card => card.OwnerUserId)
            .HasColumnName("owner_user_id");
        builder.Property(card => card.OwnershipState)
            .HasColumnName("ownership_state")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(card => card.LifecycleState)
            .HasColumnName("lifecycle_state")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(card => card.LedgerAccountId)
            .HasColumnName("ledger_account_id")
            .IsRequired();
        builder.Property(card => card.IssuanceLedgerTransactionId)
            .HasColumnName("issuance_ledger_transaction_id")
            .IsRequired();
        builder.Property(card => card.InitialValue)
            .HasColumnName("initial_value")
            .HasPrecision(20, GiftCard.AmountScale)
            .IsRequired();
        builder.Property(card => card.Currency)
            .HasColumnName("currency")
            .HasMaxLength(GiftCard.CurrencyLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(card => card.ValidFromUtc)
            .HasColumnName("valid_from_utc")
            .IsRequired();
        builder.Property(card => card.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .IsRequired();
        builder.Property(card => card.IsTransferable)
            .HasColumnName("is_transferable")
            .IsRequired();
        builder.Property(card => card.IsDivisible)
            .HasColumnName("is_divisible")
            .IsRequired();
        builder.Property(card => card.SourceGiftCardId)
            .HasColumnName("source_gift_card_id");
        builder.Property(card => card.RootGiftCardId)
            .HasColumnName("root_gift_card_id")
            .IsRequired();
        builder.Property(card => card.Generation)
            .HasColumnName("generation")
            .IsRequired();
        builder.Property(card => card.DistributionInvitationId)
            .HasColumnName("distribution_invitation_id");
        builder.Property(card => card.DistributedAtUtc)
            .HasColumnName("distributed_at_utc");
        builder.Property(card => card.ClaimedAtUtc)
            .HasColumnName("claimed_at_utc");
        builder.Property(card => card.BusinessReference)
            .HasColumnName("business_reference")
            .HasMaxLength(GiftCard.BusinessReferenceMaxLength)
            .IsRequired();
        builder.Property(card => card.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(GiftCard.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(card => card.IssuedByUserId)
            .HasColumnName("issued_by_user_id")
            .IsRequired();
        builder.Property(card => card.IssuedByMembershipId)
            .HasColumnName("issued_by_membership_id");
        builder.Property(card => card.IssuedByPartnerClientId)
            .HasColumnName("issued_by_partner_client_id");
        builder.Property(card => card.IssuedAtUtc)
            .HasColumnName("issued_at_utc")
            .IsRequired();

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(card => card.PublicReference)
            .IsUnique()
            .HasDatabaseName("ux_gift_cards_public_reference");
        builder.HasIndex(card => card.LedgerAccountId)
            .IsUnique()
            .HasDatabaseName("ux_gift_cards_ledger_account");
        builder.HasIndex(card => card.IssuanceLedgerTransactionId)
            .IsUnique()
            .HasDatabaseName("ux_gift_cards_issuance_transaction");
        builder.HasIndex(card => new { card.FundingOrganizationId, card.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_gift_cards_tenant_idempotency");
        builder.HasIndex(card => new
        {
            card.OwnerOrganizationId,
            card.OwnershipState,
            card.IssuedAtUtc,
            card.Id,
        })
            .HasDatabaseName("ix_gift_cards_organization_inventory");
        builder.HasIndex(card => new { card.OwnerUserId, card.IssuedAtUtc, card.Id })
            .HasFilter("\"owner_user_id\" IS NOT NULL")
            .HasDatabaseName("ix_gift_cards_identity_owner");
        builder.HasIndex(card => card.SourceGiftCardId)
            .HasFilter("\"source_gift_card_id\" IS NOT NULL")
            .HasDatabaseName("ix_gift_cards_source");
        builder.HasIndex(card => new { card.RootGiftCardId, card.Generation })
            .HasDatabaseName("ix_gift_cards_root_generation");
        builder.HasIndex(card => card.DistributionInvitationId)
            .IsUnique()
            .HasFilter("\"distribution_invitation_id\" IS NOT NULL")
            .HasDatabaseName("ux_gift_cards_distribution_invitation");

        builder.HasOne<GiftCard>()
            .WithMany()
            .HasForeignKey(card => card.SourceGiftCardId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GiftCardLifecycleEventConfiguration :
    IEntityTypeConfiguration<GiftCardLifecycleEvent>
{
    public void Configure(EntityTypeBuilder<GiftCardLifecycleEvent> builder)
    {
        builder.ToTable("lifecycle_events", GiftCardsDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_gift_card_lifecycle_event_actor",
                """
                ("actor_type" = 'OrganizationMember'
                    AND "actor_membership_id" IS NOT NULL)
                OR
                ("actor_type" <> 'OrganizationMember'
                    AND "actor_membership_id" IS NULL)
                """);
            table.HasCheckConstraint(
                "ck_gift_card_lifecycle_event_financial",
                """
                (
                    "action" IN ('Cancel', 'Expire')
                    AND "returned_amount" IS NOT NULL
                    AND "returned_amount" >= 0
                    AND "currency" IS NOT NULL
                    AND (
                        ("returned_amount" = 0
                            AND "ledger_transaction_id" IS NULL)
                        OR
                        ("returned_amount" > 0
                            AND "ledger_transaction_id" IS NOT NULL)
                    )
                )
                OR
                (
                    "action" IN ('Suspend', 'Reactivate')
                    AND "returned_amount" IS NULL
                    AND "currency" IS NULL
                    AND "ledger_transaction_id" IS NULL
                )
                """);
            table.HasCheckConstraint(
                "ck_gift_card_lifecycle_event_transition",
                """
                ("action" = 'Suspend' AND "new_state" = 'Suspended')
                OR
                ("action" = 'Reactivate' AND "new_state" IN ('Active', 'AwaitingClaim'))
                OR
                ("action" = 'Cancel' AND "new_state" = 'Cancelled')
                OR
                ("action" = 'Expire' AND "new_state" = 'Expired')
                """);
        });

        builder.HasKey(lifecycleEvent => lifecycleEvent.Id);
        builder.Property(lifecycleEvent => lifecycleEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(lifecycleEvent => lifecycleEvent.GiftCardId)
            .HasColumnName("gift_card_id")
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.FundingOrganizationId)
            .HasColumnName("funding_organization_id")
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.IssuingOrganizationId)
            .HasColumnName("issuing_organization_id")
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.Action)
            .HasColumnName("action")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.PreviousState)
            .HasColumnName("previous_state")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.NewState)
            .HasColumnName("new_state")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.ActorType)
            .HasColumnName("actor_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.ActorUserId)
            .HasColumnName("actor_user_id")
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.ActorMembershipId)
            .HasColumnName("actor_membership_id");
        builder.Property(lifecycleEvent => lifecycleEvent.CorrelationId)
            .HasColumnName("correlation_id")
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.Reason)
            .HasColumnName("reason")
            .HasMaxLength(GiftCardLifecycleIntent.ReasonMaxLength)
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(GiftCardLifecycleIntent.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(lifecycleEvent => lifecycleEvent.LedgerTransactionId)
            .HasColumnName("ledger_transaction_id");
        builder.Property(lifecycleEvent => lifecycleEvent.ReturnedAmount)
            .HasColumnName("returned_amount")
            .HasPrecision(20, GiftCard.AmountScale);
        builder.Property(lifecycleEvent => lifecycleEvent.Currency)
            .HasColumnName("currency")
            .HasMaxLength(GiftCard.CurrencyLength)
            .IsFixedLength();
        builder.Property(lifecycleEvent => lifecycleEvent.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .IsRequired();

        builder.HasIndex(lifecycleEvent => new
        {
            lifecycleEvent.GiftCardId,
            lifecycleEvent.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName("ux_gift_card_lifecycle_idempotency");
        builder.HasIndex(lifecycleEvent => lifecycleEvent.GiftCardId)
            .IsUnique()
            .HasFilter("\"action\" IN ('Cancel', 'Expire')")
            .HasDatabaseName("ux_gift_card_terminal_lifecycle");
        builder.HasIndex(lifecycleEvent => new
        {
            lifecycleEvent.GiftCardId,
            lifecycleEvent.OccurredAtUtc,
            lifecycleEvent.Id,
        })
            .HasDatabaseName("ix_gift_card_lifecycle_history");
        builder.HasIndex(lifecycleEvent => lifecycleEvent.LedgerTransactionId)
            .IsUnique()
            .HasFilter("\"ledger_transaction_id\" IS NOT NULL")
            .HasDatabaseName("ux_gift_card_lifecycle_ledger_transaction");

        builder.HasOne<GiftCard>()
            .WithMany()
            .HasForeignKey(lifecycleEvent => lifecycleEvent.GiftCardId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
