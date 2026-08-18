using GiftCardPlatform.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Payments.Infrastructure;

internal sealed class PaymentRefundConfiguration : IEntityTypeConfiguration<PaymentRefund>
{
    public void Configure(EntityTypeBuilder<PaymentRefund> builder)
    {
        builder.ToTable("payment_refunds", PaymentsDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_payment_refunds_amount", "\"amount\" > 0");
            table.HasCheckConstraint("ck_payment_refunds_currency", "\"currency\" ~ '^[A-Z]{3}$'");
        });
        builder.HasKey(refund => refund.Id);
        builder.Property(refund => refund.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(refund => refund.PaymentProvisionId).HasColumnName("payment_provision_id");
        builder.Property(refund => refund.RedemptionLedgerTransactionId).HasColumnName("redemption_ledger_transaction_id");
        builder.Property(refund => refund.RefundLedgerTransactionId).HasColumnName("refund_ledger_transaction_id");
        builder.Property(refund => refund.FundingOrganizationId).HasColumnName("funding_organization_id");
        builder.Property(refund => refund.GiftCardId).HasColumnName("gift_card_id");
        builder.Property(refund => refund.GiftCardPublicReference).HasColumnName("gift_card_public_reference").HasMaxLength(PaymentProvision.GiftCardPublicReferenceMaxLength);
        builder.Property(refund => refund.PosClientId).HasColumnName("pos_client_id");
        builder.Property(refund => refund.PosTerminalId).HasColumnName("pos_terminal_id");
        builder.Property(refund => refund.StoreReference).HasColumnName("store_reference").HasMaxLength(PosTerminal.StoreReferenceMaxLength);
        builder.Property(refund => refund.PosTransactionReference).HasColumnName("pos_transaction_reference").HasMaxLength(PaymentRefund.PosTransactionReferenceMaxLength);
        builder.Property(refund => refund.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(PaymentRefund.IdempotencyKeyMaxLength);
        builder.Property(refund => refund.Reason).HasColumnName("reason").HasMaxLength(PaymentRefund.ReasonMaxLength);
        builder.Property(refund => refund.Amount).HasColumnName("amount").HasColumnType("numeric(20,4)");
        builder.Property(refund => refund.Currency).HasColumnName("currency").HasMaxLength(3);
        builder.Property(refund => refund.RefundedAtUtc).HasColumnName("refunded_at_utc");
        builder.HasIndex(refund => new { refund.PaymentProvisionId, refund.IdempotencyKey })
            .IsUnique().HasDatabaseName("ux_payment_refunds_provision_idempotency");
        builder.HasIndex(refund => refund.RefundLedgerTransactionId)
            .IsUnique().HasDatabaseName("ux_payment_refunds_ledger_transaction");
        builder.HasIndex(refund => new { refund.GiftCardId, refund.RefundedAtUtc })
            .HasDatabaseName("ix_payment_refunds_card_refunded");
        builder.HasOne<PaymentProvision>().WithMany()
            .HasForeignKey(refund => refund.PaymentProvisionId).OnDelete(DeleteBehavior.Restrict);
    }
}
