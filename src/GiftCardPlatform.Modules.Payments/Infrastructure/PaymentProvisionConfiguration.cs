using GiftCardPlatform.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Payments.Infrastructure;

internal sealed class PaymentProvisionConfiguration : IEntityTypeConfiguration<PaymentProvision>
{
    public void Configure(EntityTypeBuilder<PaymentProvision> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payment_provisions", PaymentsDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_payment_provisions_amount",
                "\"amount\" > 0 AND \"amount\" <= 1000000000");

            table.HasCheckConstraint(
                "ck_payment_provisions_window",
                "\"expires_at_utc\" > \"created_at_utc\"");

            // A settled stamp exists exactly when the provision has left Active,
            // so a terminal row can never look like it is still holding value.
            table.HasCheckConstraint(
                "ck_payment_provisions_settlement",
                """
                ("state" = 'Active'
                    AND "settled_at_utc" IS NULL
                    AND "confirmed_amount" IS NULL
                    AND "redemption_ledger_transaction_id" IS NULL)
                OR
                ("state" = 'Confirmed'
                    AND "settled_at_utc" IS NOT NULL
                    AND "confirmed_amount" > 0
                    AND "confirmed_amount" <= "amount"
                    AND "redemption_ledger_transaction_id" IS NOT NULL)
                OR
                ("state" IN ('Cancelled', 'Expired')
                    AND "settled_at_utc" IS NOT NULL
                    AND "confirmed_amount" IS NULL
                    AND "redemption_ledger_transaction_id" IS NULL)
                """);

            table.HasCheckConstraint(
                "ck_payment_provisions_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(provision => provision.Id);
        builder.Property(provision => provision.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(provision => provision.PaymentTokenId)
            .HasColumnName("payment_token_id").IsRequired();
        builder.Property(provision => provision.GiftCardId)
            .HasColumnName("gift_card_id").IsRequired();
        builder.Property(provision => provision.GiftCardPublicReference)
            .HasColumnName("gift_card_public_reference")
            .HasMaxLength(PaymentProvision.GiftCardPublicReferenceMaxLength)
            .IsRequired();
        builder.Property(provision => provision.FundingOrganizationId)
            .HasColumnName("funding_organization_id").IsRequired();
        builder.Property(provision => provision.OwnerUserId)
            .HasColumnName("owner_user_id").IsRequired();
        builder.Property(provision => provision.PosClientId)
            .HasColumnName("pos_client_id").IsRequired();
        builder.Property(provision => provision.PosTerminalId)
            .HasColumnName("pos_terminal_id").IsRequired();
        builder.Property(provision => provision.StoreReference)
            .HasColumnName("store_reference")
            .HasMaxLength(PosTerminal.StoreReferenceMaxLength)
            .IsRequired();
        builder.Property(provision => provision.PosTransactionReference)
            .HasColumnName("pos_transaction_reference")
            .HasMaxLength(PaymentProvision.PosTransactionReferenceMaxLength);
        builder.Property(provision => provision.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(PaymentProvision.IdempotencyKeyMaxLength)
            .IsRequired();
        // Scoped to the client, not globally: two shops choosing the same key is
        // not a collision, and a client replaying its own key is exactly the
        // retry this exists to answer.
        builder.HasIndex(provision => new { provision.PosClientId, provision.IdempotencyKey })
            .IsUnique().HasDatabaseName("ux_payment_provisions_client_idempotency");
        builder.Property(provision => provision.Amount)
            .HasColumnName("amount").HasColumnType("numeric(20,4)").IsRequired();
        builder.Property(provision => provision.RequestedAmount)
            .HasColumnName("requested_amount").HasColumnType("numeric(20,4)").IsRequired();
        builder.Ignore(provision => provision.IsPartialApproval);
        builder.Property(provision => provision.Currency)
            .HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Property(provision => provision.State)
            .HasColumnName("state").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(provision => provision.CreatedAtUtc)
            .HasColumnName("created_at_utc").IsRequired();
        builder.Property(provision => provision.ExpiresAtUtc)
            .HasColumnName("expires_at_utc").IsRequired();
        builder.Property(provision => provision.SettledAtUtc).HasColumnName("settled_at_utc");
        builder.Property(provision => provision.ConfirmedAmount)
            .HasColumnName("confirmed_amount").HasColumnType("numeric(20,4)");
        builder.Property(provision => provision.RedemptionLedgerTransactionId)
            .HasColumnName("redemption_ledger_transaction_id");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // One credential, one hold. This is the database-level half of ADR-017
        // single use: even a concurrency defect cannot turn one scanned code
        // into two reservations.
        builder.HasIndex(provision => provision.PaymentTokenId)
            .IsUnique()
            .HasDatabaseName("ux_payment_provisions_token");

        // Availability sums active holds per card; the sweep selects due ones.
        builder.HasIndex(provision => new { provision.GiftCardId, provision.State })
            .HasDatabaseName("ix_payment_provisions_card_state");
        builder.HasIndex(provision => new { provision.State, provision.ExpiresAtUtc })
            .HasDatabaseName("ix_payment_provisions_due");
        builder.HasIndex(provision => provision.RedemptionLedgerTransactionId)
            .IsUnique()
            .HasFilter("\"redemption_ledger_transaction_id\" IS NOT NULL")
            .HasDatabaseName("ux_payment_provisions_redemption_transaction");
    }
}
