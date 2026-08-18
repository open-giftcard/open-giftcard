using GiftCardPlatform.Modules.CorporateCredits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.CorporateCredits.Infrastructure;

internal sealed class CorporateCreditAllocationConfiguration :
    IEntityTypeConfiguration<CorporateCreditAllocation>
{
    public void Configure(EntityTypeBuilder<CorporateCreditAllocation> builder)
    {
        builder.ToTable("allocations", CorporateCreditsDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_corporate_credit_allocations_amount", "\"amount\" > 0");
            table.HasCheckConstraint(
                "ck_corporate_credit_allocations_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(allocation => allocation.Id);
        builder.Property(allocation => allocation.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(allocation => allocation.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();
        builder.Property(allocation => allocation.LedgerTransactionId)
            .HasColumnName("ledger_transaction_id")
            .IsRequired();
        builder.Property(allocation => allocation.Amount)
            .HasColumnName("amount")
            .HasPrecision(20, 4)
            .IsRequired();
        builder.Property(allocation => allocation.Currency)
            .HasColumnName("currency")
            .HasMaxLength(CorporateCreditAllocation.CurrencyLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(allocation => allocation.BusinessReference)
            .HasColumnName("business_reference")
            .HasMaxLength(CorporateCreditAllocation.BusinessReferenceMaxLength)
            .IsRequired();
        builder.Property(allocation => allocation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(CorporateCreditAllocation.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(allocation => allocation.AllocatedByUserId)
            .HasColumnName("allocated_by_user_id")
            .IsRequired();
        builder.Property(allocation => allocation.AllocatedAtUtc)
            .HasColumnName("allocated_at_utc")
            .IsRequired();

        builder.HasIndex(allocation => allocation.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_corporate_credit_allocations_idempotency");
        builder.HasIndex(allocation => allocation.LedgerTransactionId)
            .IsUnique()
            .HasDatabaseName("ux_corporate_credit_allocations_ledger_transaction");
        builder.HasIndex(allocation => new { allocation.OrganizationId, allocation.AllocatedAtUtc })
            .HasDatabaseName("ix_corporate_credit_allocations_organization_allocated");
    }
}
