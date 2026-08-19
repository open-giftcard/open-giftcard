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
/// Whether every module's schema matches the build that is running.
///
/// Readiness previously proved only that PostgreSQL answered. A database can
/// answer while its schema predates the code, and the failure then surfaces as a
/// 500 on the first request that touches a missing column, long after every
/// health check has gone green. Checking the migration state here moves that
/// discovery from a user's checkout to the probe that is supposed to gate
/// traffic.
///
/// Registered as a singleton, and a healthy result is cached permanently: a
/// schema does not move backwards underneath a running process, so the steady
/// state costs nothing. An unhealthy result is deliberately not cached, so an
/// instance recovers on its next probe once migrations are applied, without
/// needing a restart.
/// </summary>
internal sealed class SchemaReadiness(IServiceProvider serviceProvider)
{
    private volatile bool schemaConfirmedCurrent;

    /// <summary>
    /// Modules whose migrations this build declares but the database has not
    /// recorded. Empty means ready.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> GetModulesBehindAsync(
        CancellationToken cancellationToken)
    {
        if (schemaConfirmedCurrent)
        {
            return [];
        }

        // Same twelve modules, and the same order, as DatabaseMigrator. Reporting
        // owns no schema and no migrations, so it is absent here too.
        var checks = new (string Module, Func<CancellationToken, Task<IReadOnlyCollection<string>>> Pending)[]
        {
            ("Organizations", serviceProvider.GetPendingOrganizationsMigrationsAsync),
            ("Audit", serviceProvider.GetPendingAuditMigrationsAsync),
            ("Authorization", serviceProvider.GetPendingAuthorizationMigrationsAsync),
            ("Identity", serviceProvider.GetPendingIdentityMigrationsAsync),
            ("Ledger", serviceProvider.GetPendingLedgerMigrationsAsync),
            ("CorporateCredits", serviceProvider.GetPendingCorporateCreditsMigrationsAsync),
            ("GiftCards", serviceProvider.GetPendingGiftCardsMigrationsAsync),
            ("Distribution", serviceProvider.GetPendingDistributionMigrationsAsync),
            ("Sharing", serviceProvider.GetPendingSharingMigrationsAsync),
            ("Payments", serviceProvider.GetPendingPaymentsMigrationsAsync),
            ("Notifications", serviceProvider.GetPendingNotificationsMigrationsAsync),
            ("Partners", serviceProvider.GetPendingPartnersMigrationsAsync),
        };

        var behind = new List<string>();
        foreach (var check in checks)
        {
            var pending = await check.Pending(cancellationToken).ConfigureAwait(false);
            if (pending.Count > 0)
            {
                behind.Add(check.Module);
            }
        }

        if (behind.Count == 0)
        {
            schemaConfirmedCurrent = true;
        }

        return behind;
    }
}
