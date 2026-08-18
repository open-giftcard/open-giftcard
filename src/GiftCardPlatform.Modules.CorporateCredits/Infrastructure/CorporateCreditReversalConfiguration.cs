using GiftCardPlatform.Modules.CorporateCredits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.CorporateCredits.Infrastructure;

internal sealed class CorporateCreditReversalConfiguration :
    IEntityTypeConfiguration<CorporateCreditReversal>
{
    public void Configure(EntityTypeBuilder<CorporateCreditReversal> builder)
    {
        builder.ToTable("reversals", CorporateCreditsDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_corporate_credit_reversals_amount",
                "\"amount\" > 0");
            table.HasCheckConstraint(
                "ck_corporate_credit_reversals_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(reversal => reversal.Id);
        builder.Property(reversal => reversal.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(reversal => reversal.AllocationId)
            .HasColumnName("allocation_id")
            .IsRequired();
        builder.Property(reversal => reversal.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();
        builder.Property(reversal => reversal.LedgerTransactionId)
            .HasColumnName("ledger_transaction_id")
            .IsRequired();
        builder.Property(reversal => reversal.Amount)
            .HasColumnName("amount")
            .HasPrecision(20, 4)
            .IsRequired();
        builder.Property(reversal => reversal.Currency)
            .HasColumnName("currency")
            .HasMaxLength(CorporateCreditAllocation.CurrencyLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(reversal => reversal.Reason)
            .HasColumnName("reason")
            .HasMaxLength(CorporateCreditReversalIntent.ReasonMaxLength)
            .IsRequired();
        builder.Property(reversal => reversal.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(CorporateCreditAllocation.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(reversal => reversal.ReversedByUserId)
            .HasColumnName("reversed_by_user_id")
            .IsRequired();
        builder.Property(reversal => reversal.ReversedAtUtc)
            .HasColumnName("reversed_at_utc")
            .IsRequired();

        builder.HasOne<CorporateCreditAllocation>()
            .WithOne()
            .HasForeignKey<CorporateCreditReversal>(reversal => reversal.AllocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(reversal => reversal.AllocationId)
            .IsUnique()
            .HasDatabaseName("ux_corporate_credit_reversals_allocation");
        builder.HasIndex(reversal => reversal.LedgerTransactionId)
            .IsUnique()
            .HasDatabaseName("ux_corporate_credit_reversals_ledger_transaction");
        builder.HasIndex(reversal => reversal.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_corporate_credit_reversals_idempotency");
        builder.HasIndex(reversal => new { reversal.OrganizationId, reversal.ReversedAtUtc })
            .HasDatabaseName("ix_corporate_credit_reversals_organization_reversed");
    }
}
