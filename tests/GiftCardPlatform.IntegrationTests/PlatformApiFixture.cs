using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.Modules.Audit;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization;
using GiftCardPlatform.Modules.CorporateCredits;
using GiftCardPlatform.Modules.Distribution;
using GiftCardPlatform.Modules.GiftCards;
using GiftCardPlatform.Modules.Identity;
using GiftCardPlatform.Modules.Ledger;
using GiftCardPlatform.Modules.Notifications;
using GiftCardPlatform.Modules.Partners;
using GiftCardPlatform.Modules.Organizations;
using GiftCardPlatform.Modules.Payments;
using GiftCardPlatform.Modules.Sharing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Shared integration-test harness (ADR-022).
///
/// Always runs against real PostgreSQL â€” never the EF InMemory or SQLite
/// providers â€” because the behaviour under test includes PostgreSQL check
/// constraints, unique indexes, ltree columns, role privileges, and (later) RLS
/// policies, none of which those providers implement.
///
/// Two modes:
///
///   Testcontainers (default)
///       Starts a disposable postgres:17 container. Requires Docker.
///
///   External PostgreSQL (opt-in)
///       Set GIFTCARD_TEST_CONNECTION to an admin-capable connection string.
///       Used when Docker is unavailable. The target database is treated as
///       disposable: its module schemas are dropped and rebuilt on every run,
///       so it must never point at a working development database. Guardrails
///       below enforce that.
///
/// In both modes the harness provisions the two roles from ADR-019 â€” a
/// migration owner that creates schemas and tables, and a runtime application
/// role the API actually uses â€” so tests exercise production privileges.
/// </summary>
public sealed class PlatformApiFixture : IAsyncLifetime
{
    public const string TestConnectionVariable = "GIFTCARD_TEST_CONNECTION";

    private const string ContainerDatabase = "giftcard";
    private const string ContainerSuperUser = "postgres";

    public const string MigratorUser = "giftcard_migrator_test";
    public const string AppUser = "giftcard_app_test";

    /// <summary>
    /// Marker written into a database the harness has adopted for testing.
    /// Its presence records that the database is disposable.
    /// </summary>
    private const string MarkerTable = "__giftcard_test_database";

    private PostgreSqlContainer? _postgres;
    private WebApplicationFactory<Program>? _factory;

    // Generated per run so no database password is ever committed or logged.
    private readonly string _migratorPassword = GeneratePassword();
    private readonly string _appPassword = GeneratePassword();
    private readonly string _jwtSigningKey =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _bootstrapSecret =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _epinDeliveryKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string AppConnectionString { get; private set; } = string.Empty;

    public string MigratorConnectionString { get; private set; } = string.Empty;

    public string Mode { get; private set; } = string.Empty;

    public string BootstrapSecret => _bootstrapSecret;

    public string CreateAccessToken(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "GiftCardPlatform",
            audience: "GiftCardPlatform.Api",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Sid, Guid.CreateVersion7().ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            ],
            notBefore: now.AddMinutes(-1).UtcDateTime,
            expires: now.AddMinutes(15).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture has not been initialised.");

    public async Task InitializeAsync()
    {
        var adminConnectionString = await ResolveAdminConnectionStringAsync();

        var template = new NpgsqlConnectionStringBuilder(adminConnectionString);
        MigratorConnectionString = Rebuild(template, MigratorUser, _migratorPassword);
        AppConnectionString = Rebuild(template, AppUser, _appPassword);

        await ProvisionAsync(adminConnectionString);
        await ApplyMigrationsAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            webHost.UseEnvironment("Development");
            webHost.UseSetting("ConnectionStrings:Default", AppConnectionString);
            webHost.UseSetting("Authentication:Jwt:SigningKey", _jwtSigningKey);
            webHost.UseSetting("Authentication:LoginRateLimit:PermitLimit", "1000");
            webHost.UseSetting(
                "Bootstrap:PlatformAdministrator:Secret",
                _bootstrapSecret);
            webHost.UseSetting("Bootstrap:RateLimit:PermitLimit", "1000");
            webHost.UseSetting("Distribution:ClaimRateLimit:PermitLimit", "1000");
            webHost.UseSetting("Payments:RedemptionRateLimit:PermitLimit", "1000");
            webHost.UseSetting("Partners:AuthRateLimit:PermitLimit", "1000");
            webHost.UseSetting("Partners:MintRateLimit:PermitLimit", "1000");
            webHost.UseSetting("Partners:CredentialFailureLimit", "5");
            webHost.UseSetting("Partners:CredentialFailureWindowSeconds", "60");
            webHost.UseSetting("Partners:EpinDeliveryKey", _epinDeliveryKey);
            webHost.UseSetting("GiftCards:Expiration:Enabled", "false");
            // Async batch processing is driven explicitly so tests can assert
            // restart and multi-instance claim behavior without worker races.
            webHost.UseSetting("Distribution:BulkBatches:Enabled", "false");
            webHost.UseSetting("Sharing:ExpirationEnabled", "false");
            webHost.UseSetting("Payments:Provisions:ExpirationEnabled", "false");
            // Notification dispatch is driven explicitly by tests, so a
            // background sweep cannot race an assertion.
            webHost.UseSetting("Notifications:DispatchEnabled", "false");
            webHost.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton<TestAuditCheckpointSigner>();
                services.AddSingleton<IAuditCheckpointSigner>(serviceProvider =>
                    serviceProvider.GetRequiredService<TestAuditCheckpointSigner>());
                services.AddSingleton<TestAuditCheckpointWitness>();
                services.AddSingleton<IAuditCheckpointWitness>(serviceProvider =>
                    serviceProvider.GetRequiredService<TestAuditCheckpointWitness>());
            });
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>Opens a connection as the runtime application role.</summary>
    public async Task<NpgsqlConnection> OpenAppConnectionAsync()
    {
        var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    // ----------------------------------------------------------------- mode

    private async Task<string> ResolveAdminConnectionStringAsync()
    {
        var external = Environment.GetEnvironmentVariable(TestConnectionVariable);

        if (!string.IsNullOrWhiteSpace(external))
        {
            Mode = "external PostgreSQL";
            return await UseExternalDatabaseAsync(external);
        }

        Mode = "Testcontainers";
        return await StartContainerAsync();
    }

    private async Task<string> StartContainerAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:17")
            .WithDatabase(ContainerDatabase)
            .WithUsername(ContainerSuperUser)
            .WithPassword(GeneratePassword())
            .Build();

        try
        {
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(BuildUnavailableMessage(ex), ex);
        }

        return _postgres.GetConnectionString();
    }

    private static string BuildUnavailableMessage(Exception ex) =>
        $"""
        Integration tests need a real PostgreSQL database, and neither option is available.

          1. Docker (default): start Docker Desktop, then re-run. Underlying error: {ex.Message.Trim()}
          2. External PostgreSQL: set {TestConnectionVariable} to an admin-capable
             connection string whose database name contains 'test', for example
             Host=localhost;Port=5432;Database=giftcard_test;Username=postgres;Password=...

        The EF InMemory and SQLite providers are not substitutes: they cannot enforce
        RLS, check constraints, unique indexes, or ltree columns.
        """;

    /// <summary>
    /// Validates that an externally supplied database is safe to treat as
    /// disposable, then adopts it. Two guardrails, both required:
    ///
    ///   * the database name must contain "test", so a normal development
    ///     database cannot be targeted by accident; and
    ///   * a marker table is written on adoption, recording that this database
    ///     is a test database.
    ///
    /// Connection details are never echoed, so passwords cannot reach logs.
    /// </summary>
    private static async Task<string> UseExternalDatabaseAsync(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;

        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"{TestConnectionVariable} is not a valid PostgreSQL connection string.", ex);
        }

        var database = builder.Database;

        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException($"{TestConnectionVariable} must specify a Database.");
        }

        if (!database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"""
                Refusing to run destructive tests against database '{database}'.

                This harness drops and rebuilds the module schemas on
                every run. To confirm the target is disposable, its name must contain 'test'
                (for example: giftcard_test). Point {TestConnectionVariable} at a dedicated
                test database.
                """);
        }

        await using var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync();
        }
        catch (NpgsqlException ex)
        {
            // Deliberately reports host/database only â€” never the full
            // connection string, which carries the password.
            throw new InvalidOperationException(
                $"Could not connect to external test database '{database}' on host '{builder.Host}'. " +
                $"Underlying error: {ex.Message}", ex);
        }

        await using var marker = new NpgsqlCommand(
            $"""
            create table if not exists public."{MarkerTable}" (
                adopted_at_utc timestamptz not null default now(),
                note text not null
            );
            insert into public."{MarkerTable}" (note)
            select 'Disposable database used by GiftCardPlatform.IntegrationTests.'
            where not exists (select 1 from public."{MarkerTable}");
            """,
            connection);

        await marker.ExecuteNonQueryAsync();

        return connectionString;
    }

    // ----------------------------------------------------------- provisioning

    private static string Rebuild(NpgsqlConnectionStringBuilder template, string username, string password) =>
        new NpgsqlConnectionStringBuilder(template.ConnectionString)
        {
            Username = username,
            Password = password,
        }.ConnectionString;

    private static string GeneratePassword() =>
        "p" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Mirrors infra/postgres/init/01-roles-and-privileges.sh.
    ///
    /// Roles are created idempotently and their passwords rotated per run.
    /// The module schemas are dropped and recreated so each run starts from a
    /// known state, which is what isolates repeated runs against an external
    /// database. Default privileges are attached to the fresh schemas before any
    /// table exists, so every table the migrator later creates grants the runtime
    /// role exactly the intended access â€” SELECT and INSERT only in the audit
    /// schema, which is what makes it append-only.
    /// </summary>
    private async Task ProvisionAsync(string adminConnectionString)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        var database = new NpgsqlConnectionStringBuilder(adminConnectionString).Database;

        var sql = $"""
            do $$
            begin
                if not exists (select 1 from pg_roles where rolname = '{MigratorUser}') then
                    create role "{MigratorUser}" login;
                end if;
                if not exists (select 1 from pg_roles where rolname = '{AppUser}') then
                    create role "{AppUser}" login;
                end if;
            end
            $$;

            alter role "{MigratorUser}" with login password '{_migratorPassword}'
                nosuperuser nocreatedb nocreaterole nobypassrls;
            alter role "{AppUser}" with login password '{_appPassword}'
                nosuperuser nocreatedb nocreaterole nobypassrls;

            grant connect on database "{database}" to "{MigratorUser}";
            grant connect on database "{database}" to "{AppUser}";

            create extension if not exists ltree;

            -- Reset module schemas so every run starts clean.
            drop schema if exists organizations   cascade;
            drop schema if exists audit           cascade;
            drop schema if exists identity        cascade;
            drop schema if exists "authorization" cascade;
            drop schema if exists ledger          cascade;
            drop schema if exists corporate_credits cascade;
            drop schema if exists gift_cards       cascade;
            drop schema if exists distribution     cascade;
            drop schema if exists sharing          cascade;
            drop schema if exists payments         cascade;
            drop schema if exists notifications    cascade;
            drop schema if exists partners         cascade;

            create schema organizations   authorization "{MigratorUser}";
            create schema audit           authorization "{MigratorUser}";
            create schema identity        authorization "{MigratorUser}";
            create schema "authorization" authorization "{MigratorUser}";
            create schema ledger          authorization "{MigratorUser}";
            create schema corporate_credits authorization "{MigratorUser}";
            create schema gift_cards       authorization "{MigratorUser}";
            create schema distribution     authorization "{MigratorUser}";
            create schema sharing          authorization "{MigratorUser}";
            create schema payments         authorization "{MigratorUser}";
            create schema notifications    authorization "{MigratorUser}";
            create schema partners         authorization "{MigratorUser}";

            grant usage on schema organizations   to "{AppUser}";
            grant usage on schema audit           to "{AppUser}";
            grant usage on schema identity        to "{AppUser}";
            grant usage on schema "authorization" to "{AppUser}";
            grant usage on schema ledger          to "{AppUser}";
            grant usage on schema corporate_credits to "{AppUser}";
            grant usage on schema gift_cards       to "{AppUser}";
            grant usage on schema distribution     to "{AppUser}";
            grant usage on schema sharing          to "{AppUser}";
            grant usage on schema payments         to "{AppUser}";
            grant usage on schema notifications    to "{AppUser}";
            grant usage on schema partners         to "{AppUser}";

            revoke create on schema organizations   from "{AppUser}";
            revoke create on schema audit           from "{AppUser}";
            revoke create on schema identity        from "{AppUser}";
            revoke create on schema "authorization" from "{AppUser}";
            revoke create on schema ledger          from "{AppUser}";
            revoke create on schema corporate_credits from "{AppUser}";
            revoke create on schema gift_cards       from "{AppUser}";
            revoke create on schema distribution     from "{AppUser}";
            revoke create on schema sharing          from "{AppUser}";
            revoke create on schema payments         from "{AppUser}";
            revoke create on schema notifications    from "{AppUser}";
            revoke create on schema partners         from "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema organizations
                grant select, insert, update, delete on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema "authorization"
                grant select, insert, update, delete on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema identity
                grant select, insert, update, delete on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema audit
                grant select, insert on tables to "{AppUser}";
            alter default privileges for role "{MigratorUser}" in schema audit
                grant usage, select on sequences to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema ledger
                grant select, insert on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema corporate_credits
                grant select, insert on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema gift_cards
                grant select, insert, update on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema distribution
                grant select, insert, update on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema sharing
                grant select, insert, update on tables to "{AppUser}";

            alter default privileges for role "{MigratorUser}" in schema payments
                grant select, insert, update on tables to "{AppUser}";

            -- The dispatcher updates state and clears the credential columns, so
            -- update is required. No delete: a settled message is operational
            -- evidence.
            alter default privileges for role "{MigratorUser}" in schema notifications
                grant select, insert, update on tables to "{AppUser}";

            -- Partner records and hashed API-client secrets. Update supports
            -- rotation and the kill switch. No delete: a retired credential is
            -- evidence and a partner anchors the funding tenant of its cards.
            alter default privileges for role "{MigratorUser}" in schema partners
                grant select, insert, update on tables to "{AppUser}";
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Applies real migrations as the migration owner, never the app role.</summary>
    private async Task ApplyMigrationsAsync()
    {
        var services = new ServiceCollection();
        services.AddBuildingBlocks(MigratorConnectionString);
        services.AddOrganizationsModule();
        services.AddAuditModule();
        services.AddAuthorizationModule();
        services.AddIdentityModule();
        services.AddLedgerModule();
        services.AddCorporateCreditsModule();
        services.AddGiftCardsModule();
        services.AddDistributionModule();
        services.AddSharingModule();
        services.AddPaymentsModule();
        services.AddNotificationsModule();
        services.AddPartnersModule();

        await using var provider = services.BuildServiceProvider();

        await provider.MigrateOrganizationsModuleAsync();
        await provider.MigrateAuditModuleAsync();

        // Also seeds the global permission catalogue from the constants.
        await provider.MigrateAuthorizationModuleAsync();
        await provider.MigrateIdentityModuleAsync();
        await provider.MigrateLedgerModuleAsync();
        await provider.MigrateCorporateCreditsModuleAsync();
        await provider.MigrateGiftCardsModuleAsync();
        await provider.MigrateDistributionModuleAsync();
        await provider.MigrateSharingModuleAsync();
        await provider.MigratePaymentsModuleAsync();
        await provider.MigrateNotificationsModuleAsync();
        await provider.MigratePartnersModuleAsync();
    }
}

// CA1711: xUnit collection-definition types conventionally end in "Collection".
#pragma warning disable CA1711
[CollectionDefinition(Name)]
public sealed class PlatformApiCollection : ICollectionFixture<PlatformApiFixture>
#pragma warning restore CA1711
{
    public const string Name = "platform-api";
}
