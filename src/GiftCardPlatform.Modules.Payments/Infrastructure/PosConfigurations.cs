using GiftCardPlatform.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GiftCardPlatform.Modules.Payments.Infrastructure;

internal sealed class PosClientConfiguration : IEntityTypeConfiguration<PosClient>
{
    public void Configure(EntityTypeBuilder<PosClient> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pos_clients", PaymentsDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_pos_clients_secret_hash",
                "\"secret_hash\" ~ '^[0-9A-F]{64}$'");
            table.HasCheckConstraint(
                "ck_pos_clients_disabled",
                "(\"status\" = 'Disabled') = (\"disabled_at_utc\" IS NOT NULL)");
        });

        builder.HasKey(client => client.Id);
        builder.Property(client => client.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(client => client.Code)
            .HasColumnName("code").HasMaxLength(PosClient.CodeMaxLength).IsRequired();
        builder.Property(client => client.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(PosClient.DisplayNameMaxLength)
            .IsRequired();
        builder.Property(client => client.SecretHash)
            .HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
        builder.Property(client => client.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(client => client.RegisteredAtUtc)
            .HasColumnName("registered_at_utc").IsRequired();
        builder.Property(client => client.DisabledAtUtc).HasColumnName("disabled_at_utc");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(client => client.Code)
            .IsUnique()
            .HasDatabaseName("ux_pos_clients_code");
    }
}

internal sealed class PosTerminalConfiguration : IEntityTypeConfiguration<PosTerminal>
{
    public void Configure(EntityTypeBuilder<PosTerminal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pos_terminals", PaymentsDbContext.Schema, table =>
            table.HasCheckConstraint(
                "ck_pos_terminals_disabled",
                "(\"status\" = 'Disabled') = (\"disabled_at_utc\" IS NOT NULL)"));

        builder.HasKey(terminal => terminal.Id);
        builder.Property(terminal => terminal.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(terminal => terminal.PosClientId)
            .HasColumnName("pos_client_id").IsRequired();
        builder.Property(terminal => terminal.Code)
            .HasColumnName("code").HasMaxLength(PosClient.CodeMaxLength).IsRequired();
        builder.Property(terminal => terminal.StoreReference)
            .HasColumnName("store_reference")
            .HasMaxLength(PosTerminal.StoreReferenceMaxLength)
            .IsRequired();
        builder.Property(terminal => terminal.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(terminal => terminal.RegisteredAtUtc)
            .HasColumnName("registered_at_utc").IsRequired();
        builder.Property(terminal => terminal.DisabledAtUtc).HasColumnName("disabled_at_utc");
        builder.Property<uint>("xmin")
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // A terminal code is unique within its client, not globally: two POS
        // vendors may both number a till "01".
        builder.HasIndex(terminal => new { terminal.PosClientId, terminal.Code })
            .IsUnique()
            .HasDatabaseName("ux_pos_terminals_client_code");

        builder.HasOne<PosClient>()
            .WithMany()
            .HasForeignKey(terminal => terminal.PosClientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
