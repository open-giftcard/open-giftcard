using GiftCardPlatform.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Payments.Infrastructure;

internal sealed class PaymentTokenConfiguration : IEntityTypeConfiguration<PaymentToken>
{
    public void Configure(EntityTypeBuilder<PaymentToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payment_tokens", PaymentsDbContext.Schema, table =>
        {
            // A credential that never expires would defeat the whole point.
            table.HasCheckConstraint(
                "ck_payment_tokens_expiry",
                "\"expires_at_utc\" > \"issued_at_utc\"");

            // Consumption cannot predate issuance.
            table.HasCheckConstraint(
                "ck_payment_tokens_consumption",
                "\"consumed_at_utc\" IS NULL OR \"consumed_at_utc\" >= \"issued_at_utc\"");

            // Exactly 64 hex characters: a SHA-256 digest and nothing else. This
            // is what stops a raw secret being written into the column.
            table.HasCheckConstraint(
                "ck_payment_tokens_secret_hash",
                "\"secret_hash\" ~ '^[0-9A-F]{64}$'");
            table.HasCheckConstraint(
                "ck_payment_tokens_numeric_code_hash",
                "\"numeric_code_hash\" IS NULL OR \"numeric_code_hash\" ~ '^[0-9A-F]{64}$'");
        });

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(token => token.GiftCardId).HasColumnName("gift_card_id").IsRequired();
        builder.Property(token => token.FundingOrganizationId)
            .HasColumnName("funding_organization_id").IsRequired();
        builder.Property(token => token.OwnerUserId).HasColumnName("owner_user_id").IsRequired();
        builder.Property(token => token.SecretHash)
            .HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
        builder.Property(token => token.NumericCodeHash)
            .HasColumnName("numeric_code_hash").HasMaxLength(64);
        builder.Property(token => token.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();
        builder.Property(token => token.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(token => token.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Redemption resolves a presented credential by its identifier, then
        // verifies the secret in constant time. Owner listing and the eventual
        // expiry sweep both read by card and expiry.
        builder.HasIndex(token => new { token.GiftCardId, token.ExpiresAtUtc })
            .HasDatabaseName("ix_payment_tokens_card_expiry");
        builder.HasIndex(token => token.OwnerUserId)
            .HasDatabaseName("ix_payment_tokens_owner");
        builder.HasIndex(token => token.NumericCodeHash)
            .IsUnique()
            .HasFilter("\"numeric_code_hash\" IS NOT NULL")
            .HasDatabaseName("ux_payment_tokens_numeric_code_hash");
    }
}
