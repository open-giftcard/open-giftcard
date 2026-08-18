using GiftCardPlatform.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Identity.Infrastructure;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable(
            "users",
            IdentityDbContext.Schema,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_users_status",
                    "status in ('Active', 'Disabled')");
                table.HasCheckConstraint(
                    "ck_users_disabled_state",
                    "(status = 'Active' and disabled_at_utc is null) or " +
                    "(status = 'Disabled' and disabled_at_utc is not null)");
                table.HasCheckConstraint(
                    "ck_users_contact",
                    """
                    ("email" IS NOT NULL
                        AND "normalized_email" IS NOT NULL
                        AND "phone_number" IS NULL
                        AND "normalized_phone_number" IS NULL)
                    OR
                    ("email" IS NULL
                        AND "normalized_email" IS NULL
                        AND "phone_number" IS NOT NULL
                        AND "normalized_phone_number" IS NOT NULL)
                    """);
            });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(320);
        builder.Property(x => x.PhoneNumber).HasColumnName("phone_number").HasMaxLength(16);
        builder.Property(x => x.NormalizedPhoneNumber)
            .HasColumnName("normalized_phone_number")
            .HasMaxLength(16);
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.DisabledAtUtc).HasColumnName("disabled_at_utc");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(x => x.NormalizedEmail)
            .IsUnique()
            .HasFilter("\"normalized_email\" IS NOT NULL")
            .HasDatabaseName("ux_users_normalized_email");
        builder.HasIndex(x => x.NormalizedPhoneNumber)
            .IsUnique()
            .HasFilter("\"normalized_phone_number\" IS NOT NULL")
            .HasDatabaseName("ux_users_normalized_phone");
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable(
            "sessions",
            IdentityDbContext.Schema,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_sessions_expiry",
                    "expires_at_utc > created_at_utc");
                table.HasCheckConstraint(
                    "ck_sessions_revocation_state",
                    "(revoked_at_utc is null and revocation_reason is null) or " +
                    "(revoked_at_utc is not null and revocation_reason is not null)");
            });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.TokenFamilyId).HasColumnName("token_family_id").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(x => x.RevocationReason).HasColumnName("revocation_reason").HasMaxLength(64);

        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_sessions_user_id");
        builder.HasIndex(x => x.TokenFamilyId)
            .IsUnique()
            .HasDatabaseName("ux_sessions_token_family_id");
        builder.HasAlternateKey(x => new { x.Id, x.TokenFamilyId })
            .HasName("ak_sessions_id_token_family");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable(
            "refresh_tokens",
            IdentityDbContext.Schema,
            table =>
            {
                table.HasCheckConstraint(
                    "ck_refresh_tokens_expiry",
                    "expires_at_utc > created_at_utc");
                table.HasCheckConstraint(
                    "ck_refresh_tokens_consumed_state",
                    "(consumed_at_utc is null and replaced_by_token_id is null) or " +
                    "(consumed_at_utc is not null and replaced_by_token_id is not null)");
            });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(x => x.TokenFamilyId).HasColumnName("token_family_id").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(x => x.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(x => x.ReplacedByTokenId).HasColumnName("replaced_by_token_id");

        builder.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_refresh_tokens_token_hash");
        builder.HasIndex(x => new { x.SessionId, x.TokenFamilyId })
            .HasDatabaseName("ix_refresh_tokens_session_family");

        builder.HasOne<UserSession>()
            .WithMany()
            .HasForeignKey(x => new { x.SessionId, x.TokenFamilyId })
            .HasPrincipalKey(x => new { x.Id, x.TokenFamilyId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
