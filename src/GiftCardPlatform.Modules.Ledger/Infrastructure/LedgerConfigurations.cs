using GiftCardPlatform.Modules.Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Ledger.Infrastructure;

internal sealed class LedgerAccountConfiguration : IEntityTypeConfiguration<LedgerAccount>
{
    public void Configure(EntityTypeBuilder<LedgerAccount> builder)
    {
        builder.ToTable("accounts", LedgerDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_ledger_accounts_scope",
                """
                ("type" = 'PlatformFunding'
                    AND "organization_id" IS NULL
                    AND "gift_card_id" IS NULL)
                OR
                ("type" = 'PlatformRedemptionSettlement'
                    AND "organization_id" IS NULL
                    AND "gift_card_id" IS NULL)
                OR
                ("type" = 'OrganizationCorporateCredit'
                    AND "organization_id" IS NOT NULL
                    AND "gift_card_id" IS NULL)
                OR
                ("type" = 'GiftCardValue'
                    AND "organization_id" IS NOT NULL
                    AND "gift_card_id" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "ck_ledger_accounts_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(account => account.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(48)
            .IsRequired();
        builder.Property(account => account.OrganizationId).HasColumnName("organization_id");
        builder.Property(account => account.GiftCardId).HasColumnName("gift_card_id");
        builder.Property(account => account.Currency)
            .HasColumnName("currency")
            .HasMaxLength(Money.CurrencyLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(account => account.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(account => new { account.Type, account.Currency })
            .IsUnique()
            .HasFilter("\"organization_id\" IS NULL")
            .HasDatabaseName("ux_ledger_platform_account");
        builder.HasIndex(account => new { account.OrganizationId, account.Type, account.Currency })
            .IsUnique()
            .HasFilter("\"type\" = 'OrganizationCorporateCredit'")
            .HasDatabaseName("ux_ledger_organization_account");
        builder.HasIndex(account => account.GiftCardId)
            .IsUnique()
            .HasFilter("\"gift_card_id\" IS NOT NULL")
            .HasDatabaseName("ux_ledger_gift_card_account");
    }
}

internal sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.ToTable("transactions", LedgerDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_ledger_transactions_organization",
                "\"organization_id\" <> '00000000-0000-0000-0000-000000000000'");
        });

        builder.HasKey(transaction => transaction.Id);
        builder.Property(transaction => transaction.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(transaction => transaction.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();
        builder.Property(transaction => transaction.OperationType)
            .HasColumnName("operation_type")
            .HasMaxLength(LedgerTransaction.OperationTypeMaxLength)
            .IsRequired();
        builder.Property(transaction => transaction.BusinessReference)
            .HasColumnName("business_reference")
            .HasMaxLength(LedgerTransaction.BusinessReferenceMaxLength)
            .IsRequired();
        builder.Property(transaction => transaction.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(LedgerTransaction.IdempotencyKeyMaxLength)
            .IsRequired();
        builder.Property(transaction => transaction.IntentHash)
            .HasColumnName("intent_hash")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        builder.Property(transaction => transaction.ReversesTransactionId)
            .HasColumnName("reverses_transaction_id");
        builder.Property(transaction => transaction.InitiatedByUserId)
            .HasColumnName("initiated_by_user_id")
            .IsRequired();
        builder.Property(transaction => transaction.PostedAtUtc)
            .HasColumnName("posted_at_utc")
            .IsRequired();

        builder.HasIndex(transaction => new { transaction.OperationType, transaction.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_ledger_transactions_operation_idempotency");
        builder.HasIndex(transaction => new { transaction.OrganizationId, transaction.PostedAtUtc })
            .HasDatabaseName("ix_ledger_transactions_organization_posted");
        builder.HasIndex(transaction => transaction.ReversesTransactionId)
            .IsUnique()
            .HasFilter("\"reverses_transaction_id\" IS NOT NULL")
            .HasDatabaseName("ux_ledger_transactions_reversal");

        builder.HasOne<LedgerTransaction>()
            .WithMany()
            .HasForeignKey(transaction => transaction.ReversesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(transaction => transaction.Entries)
            .WithOne()
            .HasForeignKey(entry => entry.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(transaction => transaction.Entries)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("entries", LedgerDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_ledger_entries_amount", "\"amount\" > 0");
            table.HasCheckConstraint(
                "ck_ledger_entries_direction",
                "\"direction\" IN ('Debit', 'Credit')");
            table.HasCheckConstraint(
                "ck_ledger_entries_currency",
                "\"currency\" ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.TransactionId).HasColumnName("transaction_id").IsRequired();
        builder.Property(entry => entry.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(entry => entry.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(entry => entry.Direction)
            .HasColumnName("direction")
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();
        builder.Property(entry => entry.Amount)
            .HasColumnName("amount")
            .HasPrecision(20, Money.Scale)
            .IsRequired();
        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .HasMaxLength(Money.CurrencyLength)
            .IsFixedLength()
            .IsRequired();

        builder.HasOne<LedgerAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entry => new { entry.TransactionId, entry.Direction })
            .HasDatabaseName("ix_ledger_entries_transaction_direction");
        builder.HasIndex(entry => new { entry.OrganizationId, entry.AccountId })
            .HasDatabaseName("ix_ledger_entries_organization_account");
    }
}
