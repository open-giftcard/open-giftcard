using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Exercises the demonstration seed against real PostgreSQL.
///
/// The seed exists so a clone reaches a populated screen, but it posts to the
/// Ledger to get there, and a demonstration that quietly produced unbalanced or
/// duplicated money would be worse than no demonstration at all. These tests
/// assert the financial outcome rather than that the seed merely ran.
///
/// Each run uses codes unique to itself. The organization code is the seed's
/// idempotency key, and the till client code is platform-scoped and unique, so
/// sharing either with another run would make the result depend on test order.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class DemoSeedTests(PlatformApiFixture fixture)
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    // This host is independent of the fixture's own, so it carries its own keys
    // rather than widening the fixture's surface for a test's convenience.
    private readonly string _signingKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private readonly string _epinDeliveryKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private string OrganizationCode => $"SEEDTEST-{_suffix}";

    private const decimal Funding = 50_000m;
    private const decimal CardAmount = 250m;
    private const decimal PaymentAmount = 90m;
    private const decimal RefundAmount = 30m;

    // A silently failing seed is undiagnosable in CI, so the host's log records
    // are captured and reported when an assertion fails.
    private readonly CapturedLogs _logs = new();

    private WebApplicationFactory<Program> SeedingHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            webHost.ConfigureLogging(logging => logging.AddProvider(_logs));
            webHost.UseEnvironment("Development");
            webHost.UseSetting("ConnectionStrings:Default", fixture.AppConnectionString);
            webHost.UseSetting("Authentication:Jwt:SigningKey", _signingKey);
            webHost.UseSetting("Partners:EpinDeliveryKey", _epinDeliveryKey);
            webHost.UseSetting("Bootstrap:PlatformAdministrator:Secret", fixture.BootstrapSecret);

            webHost.UseSetting("Demo:Seed:Enabled", "true");
            webHost.UseSetting("Demo:Seed:OrganizationCode", OrganizationCode);
            webHost.UseSetting("Demo:Seed:FirstSubsidiaryCode", $"{OrganizationCode}-A");
            webHost.UseSetting("Demo:Seed:SecondSubsidiaryCode", $"{OrganizationCode}-B");
            webHost.UseSetting("Demo:Seed:TillClientCode", $"TILL-{_suffix}");
            webHost.UseSetting("Demo:Seed:PlatformAdministratorEmail", $"seed.platform.{_suffix}@example.test");
            webHost.UseSetting("Demo:Seed:CompanyAdministratorEmail", $"seed.company.{_suffix}@example.test");
            webHost.UseSetting("Demo:Seed:RecipientEmail", $"seed.recipient.{_suffix}@example.test");
            webHost.UseSetting("Demo:Seed:FundingAmount", Funding.ToString(CultureInfo.InvariantCulture));
            webHost.UseSetting("Demo:Seed:GiftCardAmount", CardAmount.ToString(CultureInfo.InvariantCulture));
            webHost.UseSetting("Demo:Seed:PaymentAmount", PaymentAmount.ToString(CultureInfo.InvariantCulture));
            webHost.UseSetting("Demo:Seed:RefundAmount", RefundAmount.ToString(CultureInfo.InvariantCulture));

            // Background workers would otherwise race the assertions.
            webHost.UseSetting("GiftCards:Expiration:Enabled", "false");
            webHost.UseSetting("Distribution:BulkBatches:Enabled", "false");
            webHost.UseSetting("Sharing:ExpirationEnabled", "false");
            webHost.UseSetting("Payments:Provisions:ExpirationEnabled", "false");

            // The dispatcher stays on. The seed claims the card using the
            // activation link captured by the Development notification sink, and
            // that sink is only populated once the outbox message is dispatched.
            webHost.UseSetting("Notifications:DispatchEnabled", "true");
        });

    /// <summary>
    /// Starts a host and waits for the seed, which runs as a background service
    /// and so is not finished when the host is ready.
    /// </summary>
    private async Task RunSeedAsync()
    {
        await using var factory = SeedingHost();
        _ = factory.Services;

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await OrganizationIdAsync().ConfigureAwait(false) is not null)
            {
                // The organization appears first; wait for the refund, which is last.
                if (await ScalarAsync<long>(
                        "select count(*) from payments.payment_refunds r " +
                        "join payments.payment_provisions p on p.id = r.payment_provision_id " +
                        "join gift_cards.gift_cards g on g.id = p.gift_card_id " +
                        "where g.funding_organization_id = @org",
                        await OrganizationIdAsync().ConfigureAwait(false)).ConfigureAwait(false) > 0)
                {
                    return;
                }
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        Assert.Fail(
            $"The demo seed did not complete within 90 seconds for organization " +
            $"{OrganizationCode}.{Environment.NewLine}{_logs.Dump()}");
    }

    // Tenant tables use FORCE row level security, so even the owning role sees
    // nothing without an RLS session context. ScopedSqlSession establishes one,
    // which is the same requirement the application itself lives under.
    private async Task<Guid?> OrganizationIdAsync()
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture)
            .ConfigureAwait(false);

        await using var command = session.Command(
            "select id from organizations.organizations where code = @code");
        command.Parameters.AddWithValue("code", OrganizationCode);

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is Guid id ? id : null;
    }

    private async Task<T> ScalarAsync<T>(string sql, object? organizationParameter = null)
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture)
            .ConfigureAwait(false);

        await using var command = session.Command(sql);
        if (organizationParameter is not null)
        {
            command.Parameters.AddWithValue("org", organizationParameter);
        }

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull
            ? default!
            : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Seed_produces_a_balanced_ledger_and_a_derived_card_balance()
    {
        await RunSeedAsync();

        var organizationId = await OrganizationIdAsync();
        Assert.NotNull(organizationId);

        // The organization and both of its children exist.
        Assert.Equal(
            2,
            await ScalarAsync<long>(
                "select count(*) from organizations.organizations " +
                "where parent_organization_id = @org",
                organizationId));

        // Every transaction this seed posted balances, per currency. This is the
        // invariant the whole financial model rests on.
        var unbalanced = await ScalarAsync<long>(
            """
            select count(*) from (
              select t.id
              from ledger.transactions t
              join ledger.entries e on e.transaction_id = t.id
              where t.organization_id = @org
              group by t.id, e.currency
              having sum(case when e.direction = 'Credit' then e.amount else -e.amount end) <> 0
            ) unbalanced
            """,
            organizationId);
        Assert.Equal(0, unbalanced);

        // The spent card's remaining value is derived from ledger entries, not
        // read from a column: funded, less what the till confirmed, plus the
        // refund. Scoped to the claimed card, because the seed also leaves an
        // untouched card in inventory so the portal's inventory screen is not
        // empty, and summing both would assert nothing about either.
        var derived = await ScalarAsync<decimal>(
            """
            select coalesce(sum(case when e.direction = 'Credit' then e.amount else -e.amount end), 0)
            from ledger.entries e
            join ledger.accounts a on a.id = e.account_id
            join gift_cards.gift_cards g on g.id = a.gift_card_id
            where g.funding_organization_id = @org and g.owner_user_id is not null
            """,
            organizationId);
        Assert.Equal(CardAmount - PaymentAmount + RefundAmount, derived);

        // The untouched card is still in organization inventory at full value.
        // Without it the portal's first tab shows an empty state, which is how
        // this was found.
        Assert.Equal(
            CardAmount,
            await ScalarAsync<decimal>(
                """
                select coalesce(sum(case when e.direction = 'Credit' then e.amount else -e.amount end), 0)
                from ledger.entries e
                join ledger.accounts a on a.id = e.account_id
                join gift_cards.gift_cards g on g.id = a.gift_card_id
                where g.funding_organization_id = @org and g.owner_user_id is null
                """,
                organizationId));

        Assert.Equal(
            2,
            await ScalarAsync<long>(
                "select count(*) from gift_cards.gift_cards where funding_organization_id = @org",
                organizationId));

        Assert.Equal(
            PaymentAmount,
            await ScalarAsync<decimal>(
                "select coalesce(sum(p.confirmed_amount), 0) from payments.payment_provisions p " +
                "join gift_cards.gift_cards g on g.id = p.gift_card_id " +
                "where g.funding_organization_id = @org",
                organizationId));

        Assert.Equal(
            RefundAmount,
            await ScalarAsync<decimal>(
                "select coalesce(sum(r.amount), 0) from payments.payment_refunds r " +
                "join payments.payment_provisions p on p.id = r.payment_provision_id " +
                "join gift_cards.gift_cards g on g.id = p.gift_card_id " +
                "where g.funding_organization_id = @org",
                organizationId));

        // The card left organization inventory and is owned by the recipient the
        // claim created, which is what makes the demonstration look real.
        Assert.Equal(
            1,
            await ScalarAsync<long>(
                "select count(*) from gift_cards.gift_cards " +
                "where funding_organization_id = @org and owner_user_id is not null",
                organizationId));
    }

    [Fact]
    public async Task Seed_is_idempotent_and_does_not_post_twice()
    {
        await RunSeedAsync();

        var organizationId = await OrganizationIdAsync();
        Assert.NotNull(organizationId);

        const string TransactionCount =
            "select count(*) from ledger.transactions where organization_id = @org";
        const string EntrySum =
            """
            select coalesce(sum(case when direction = 'Credit' then amount else -amount end), 0)
            from ledger.entries where organization_id = @org
            """;

        var transactionsBefore = await ScalarAsync<long>(TransactionCount, organizationId)
;
        var netBefore = await ScalarAsync<decimal>(EntrySum, organizationId);

        Assert.True(transactionsBefore > 0, "The first seed run posted no ledger transactions.");

        // A second host against the same database must find its own work and stop.
        await using (var second = SeedingHost())
        {
            _ = second.Services;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(
            transactionsBefore,
            await ScalarAsync<long>(TransactionCount, organizationId));
        Assert.Equal(
            netBefore,
            await ScalarAsync<decimal>(EntrySum, organizationId));

        // And it did not create a second organization under the same code.
        Assert.Equal(
            1,
            await ScalarAsync<long>(
                "select count(*) from organizations.organizations where code = @org",
                OrganizationCode));
    }
}

/// <summary>Minimal in-memory log sink, so a failing seed explains itself.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _records = new();

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _records);

    public void Dispose()
    {
    }

    public string Dump()
    {
        var interesting = _records
            .Where(record =>
                record.Contains("Demo", StringComparison.OrdinalIgnoreCase) ||
                record.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return interesting.Length == 0
            ? "(no demo seed log records were captured)"
            : string.Join(Environment.NewLine, interesting);
    }

    private sealed class Sink(
        string category,
        System.Collections.Concurrent.ConcurrentQueue<string> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            records.Enqueue(exception is null
                ? $"[{logLevel}] {category}: {message}"
                : $"[{logLevel}] {category}: {message} :: {exception}");
        }
    }
}
