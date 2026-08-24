using GiftCardPlatform.Modules.Partners.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Partners.Infrastructure;

internal sealed class PartnerConfiguration : IEntityTypeConfiguration<Partner>
{
    public void Configure(EntityTypeBuilder<Partner> builder)
    {
        builder.ToTable("partners", PartnersDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_partners_code", "\"code\" ~ '^[A-Z0-9-]+$'");
            table.HasCheckConstraint(
                "ck_partners_status",
                """
                ("status" = 'Active' AND "disabled_at_utc" IS NULL)
                OR
                ("status" = 'Disabled' AND "disabled_at_utc" IS NOT NULL)
                """);
        });

        builder.HasKey(partner => partner.Id);
        builder.Property(partner => partner.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(partner => partner.RootOrganizationId).HasColumnName("root_organization_id").IsRequired();
        builder.Property(partner => partner.Code)
            .HasColumnName("code").HasMaxLength(Partner.CodeMaxLength).IsRequired();
        builder.Property(partner => partner.DisplayName)
            .HasColumnName("display_name").HasMaxLength(Partner.DisplayNameMaxLength).IsRequired();
        builder.Property(partner => partner.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(partner => partner.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();
        builder.Property(partner => partner.DisabledAtUtc).HasColumnName("disabled_at_utc");

        builder.HasIndex(partner => partner.Code)
            .HasDatabaseName("ux_partners_code").IsUnique();

        // One partner per funding tenant. Two partner rows on one organization
        // would make the corporate-credit balance a shared pool with no way to
        // attribute a mint, and would defeat per-partner velocity accounting.
        builder.HasIndex(partner => partner.RootOrganizationId)
            .HasDatabaseName("ux_partners_root_organization").IsUnique();
    }
}

internal sealed class PartnerApiClientConfiguration : IEntityTypeConfiguration<PartnerApiClient>
{
    public void Configure(EntityTypeBuilder<PartnerApiClient> builder)
    {
        builder.ToTable("api_clients", PartnersDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_partner_api_clients_code", "\"code\" ~ '^[A-Z0-9-]+$'");
            table.HasCheckConstraint(
                "ck_partner_api_clients_secret_hash",
                "\"secret_hash\" ~ '^[0-9A-F]{64}$'");
            // A credential that can authenticate but do nothing is a
            // misconfiguration, not a deliberate state.
            table.HasCheckConstraint(
                "ck_partner_api_clients_scopes",
                "cardinality(\"scopes\") >= 1");
            table.HasCheckConstraint(
                "ck_partner_api_clients_status",
                """
                ("status" = 'Active' AND "disabled_at_utc" IS NULL)
                OR
                ("status" = 'Disabled' AND "disabled_at_utc" IS NOT NULL)
                """);
        });

        builder.HasKey(client => client.Id);
        builder.Property(client => client.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(client => client.PartnerId).HasColumnName("partner_id").IsRequired();
        builder.Property(client => client.RootOrganizationId).HasColumnName("root_organization_id").IsRequired();
        builder.Property(client => client.Code)
            .HasColumnName("code").HasMaxLength(PartnerApiClient.CodeMaxLength).IsRequired();
        builder.Property(client => client.DisplayName)
            .HasColumnName("display_name").HasMaxLength(PartnerApiClient.DisplayNameMaxLength).IsRequired();
        builder.Property(client => client.Scopes)
            .HasColumnName("scopes").HasColumnType("text[]").IsRequired();
        builder.Property(client => client.SecretHash)
            .HasColumnName("secret_hash").HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(client => client.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(client => client.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();
        builder.Property(client => client.DisabledAtUtc).HasColumnName("disabled_at_utc");

        builder.HasIndex(client => client.Code)
            .HasDatabaseName("ux_partner_api_clients_code").IsUnique();
        builder.HasIndex(client => client.PartnerId)
            .HasDatabaseName("ix_partner_api_clients_partner");

        builder.HasOne<Partner>()
            .WithMany()
            .HasForeignKey(client => client.PartnerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PartnerMintRateWindowConfiguration :
    IEntityTypeConfiguration<PartnerMintRateWindow>
{
    public void Configure(EntityTypeBuilder<PartnerMintRateWindow> builder)
    {
        builder.ToTable("mint_rate_windows", PartnersDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_partner_mint_rate_windows_request_count",
                "request_count > 0");
        });

        builder.HasKey(window => window.PartnerApiClientId);
        builder.Property(window => window.PartnerApiClientId)
            .HasColumnName("partner_api_client_id");
        builder.Property(window => window.WindowStartedAtUtc)
            .HasColumnName("window_started_at_utc");
        builder.Property(window => window.RequestCount)
            .HasColumnName("request_count");
        builder.HasOne<PartnerApiClient>()
            .WithMany()
            .HasForeignKey(window => window.PartnerApiClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
