using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.Modules.Audit;
using GiftCardPlatform.Modules.Authorization;
using GiftCardPlatform.Modules.CorporateCredits;
using GiftCardPlatform.Modules.Distribution;
using GiftCardPlatform.Modules.GiftCards;
using GiftCardPlatform.Modules.Identity;
using GiftCardPlatform.Modules.Ledger;
using GiftCardPlatform.Modules.Notifications;
using GiftCardPlatform.Modules.Organizations;
using GiftCardPlatform.Modules.Partners;
using GiftCardPlatform.Modules.Payments;
using GiftCardPlatform.Modules.Sharing;

namespace GiftCardPlatform.Api;

/// <summary>
/// Applies every module's migrations and exits. Selected with <c>--migrate</c>.
///
/// This exists so a container image can bring a database up to date without
/// shipping the .NET SDK and <c>dotnet ef</c>, and so the twelve commands the
/// README lists become one step that cannot be run in the wrong order or
/// half-finished.
///
/// It deliberately does not run inside the API. Migrations must execute as the
/// migration owner, and the API runs as a non-superuser role that owns nothing;
/// the two roles exist precisely so the running application cannot alter its own
/// schema (ADR-019). This entry point therefore rebinds the default connection to
/// <c>GIFTCARD_MIGRATIONS_CONNECTION</c> for its own process only, and refuses to
/// start without it rather than silently connecting as the application role,
/// which no-ops on applied modules and then fails on the first new one with a
/// permission error that reads like an unrelated bug.
/// </summary>
internal static class DatabaseMigrator
{
    public const string Switch = "--migrate";

    public const string ConnectionVariable = "GIFTCARD_MIGRATIONS_CONNECTION";

    public static bool IsRequested(string[] args) =>
        args.Contains(Switch, StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // The Windows default provider set includes Event Log. An unprivileged
        // deployment account may not write it, and that logging failure can
        // replace the real migration exception. Migration output is an operator
        // stream, so keep it deterministic and console-only on every platform.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();

        var migrationsConnection = builder.Configuration[ConnectionVariable];
        if (string.IsNullOrWhiteSpace(migrationsConnection))
        {
            Console.Error.WriteLine(
                $"{ConnectionVariable} is required for {Switch}. Set it to the migration owner's " +
                "connection string. The application role cannot apply migrations: it owns nothing " +
                "and holds no DDL privilege.");
            return 2;
        }

        // Modules resolve their DbContext from ConnectionStrings:Default. In this
        // process that is the migration owner, and this process serves nothing.
        builder.Configuration["ConnectionStrings:Default"] = migrationsConnection;

        builder.Services.AddBuildingBlocks(migrationsConnection);
        builder.Services.AddOrganizationsModule(builder.Configuration);
        builder.Services.AddAuditModule(builder.Configuration);
        builder.Services.AddAuthorizationModule(builder.Configuration);
        builder.Services.AddIdentityModule(builder.Configuration);
        builder.Services.AddLedgerModule();
        builder.Services.AddCorporateCreditsModule();
        builder.Services.AddGiftCardsModule();
        builder.Services.AddDistributionModule(builder.Configuration);
        builder.Services.AddSharingModule(builder.Configuration);
        builder.Services.AddPaymentsModule(builder.Configuration);
        builder.Services.AddPartnersModule(builder.Configuration);
        builder.Services.AddNotificationsModule(builder.Configuration);

        using var host = builder.Build();
        var services = host.Services;

        // Ordered so a module that expects another's schema to exist finds it.
        // Reporting is absent on purpose: it owns no schema and no migrations.
        var steps = new (string Module, Func<Task> Apply)[]
        {
            ("Organizations", () => services.MigrateOrganizationsModuleAsync()),
            ("Audit", () => services.MigrateAuditModuleAsync()),
            ("Authorization", () => services.MigrateAuthorizationModuleAsync()),
            ("Identity", () => services.MigrateIdentityModuleAsync()),
            ("Ledger", () => services.MigrateLedgerModuleAsync()),
            ("CorporateCredits", () => services.MigrateCorporateCreditsModuleAsync()),
            ("GiftCards", () => services.MigrateGiftCardsModuleAsync()),
            ("Distribution", () => services.MigrateDistributionModuleAsync()),
            ("Sharing", () => services.MigrateSharingModuleAsync()),
            ("Payments", () => services.MigratePaymentsModuleAsync()),
            ("Notifications", () => services.MigrateNotificationsModuleAsync()),
            ("Partners", () => services.MigratePartnersModuleAsync()),
        };

        foreach (var step in steps)
        {
            try
            {
                await step.Apply().ConfigureAwait(false);
                Console.WriteLine($"migrated {step.Module}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"failed migrating {step.Module}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"all {steps.Length} module migrations applied");
        return 0;
    }
}
