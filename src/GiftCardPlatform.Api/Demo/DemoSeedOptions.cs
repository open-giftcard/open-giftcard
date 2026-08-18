using System.ComponentModel.DataAnnotations;

namespace GiftCardPlatform.Api.Demo;

/// <summary>
/// Configuration for the Development-only demonstration seed.
///
/// The seed exists so that a clone reaches a populated screen without anyone
/// having to learn the administrative workflows first. It is not a fixture
/// loader and not a migration: it drives the same application services an
/// operator would, so what a visitor sees is produced by the real issuance,
/// ledger, distribution, and payment paths.
///
/// Two independent gates keep it out of a deployment. The hosted service is
/// registered only on the Development branch of <c>Program</c>, exactly like
/// <c>/demo</c> and <c>/swagger</c>, so outside Development the type is never
/// constructed. <see cref="Enabled"/> then has to be turned on deliberately.
/// Neither gate implies the other.
/// </summary>
public sealed class DemoSeedOptions
{
    public const string SectionName = "Demo:Seed";

    /// <summary>
    /// Off by default. Turning this on in a non-Development environment does
    /// nothing, because the hosted service is not registered there at all.
    /// </summary>
    public bool Enabled { get; set; }

    [EmailAddress]
    public string PlatformAdministratorEmail { get; set; } = "platform.admin@example.test";

    [EmailAddress]
    public string CompanyAdministratorEmail { get; set; } = "company.admin@example.test";

    [EmailAddress]
    public string RecipientEmail { get; set; } = "recipient@example.test";

    /// <summary>
    /// Shared by the seeded accounts so the credentials are easy to publish in a
    /// README. Development only; the password policy still applies to it.
    /// </summary>
    public string Password { get; set; } = "Demo passphrase 2026!";

    public string OrganizationName { get; set; } = "Northwind Trading";

    /// <summary>
    /// Also the seed's idempotency key. A root organization already carrying this
    /// code means the seed has run, and it returns without touching anything.
    /// </summary>
    public string OrganizationCode { get; set; } = "DEMO-NORTHWIND";

    public string FirstSubsidiaryName { get; set; } = "Northwind Engineering";

    public string FirstSubsidiaryCode { get; set; } = "DEMO-NORTHWIND-ENG";

    public string SecondSubsidiaryName { get; set; } = "Northwind Sales";

    public string SecondSubsidiaryCode { get; set; } = "DEMO-NORTHWIND-SLS";

    /// <summary>
    /// Platform-scoped and unique, so it is configurable for the same reason the
    /// organization code is: a test needs to seed without colliding with data a
    /// previous run left behind.
    /// </summary>
    public string TillClientCode { get; set; } = "DEMO-TILL";

    public string TillTerminalCode { get; set; } = "T-01";

    public string TillStoreReference { get; set; } = "STORE-DEMO";

    public string Currency { get; set; } = "USD";

    /// <summary>Corporate credit allocated to the organization.</summary>
    public decimal FundingAmount { get; set; } = 50_000m;

    /// <summary>Face value of the single card issued and distributed.</summary>
    public decimal GiftCardAmount { get; set; } = 250m;

    /// <summary>Amount confirmed at the till, which must not exceed the card.</summary>
    public decimal PaymentAmount { get; set; } = 90m;

    /// <summary>Partial refund against the confirmed payment.</summary>
    public decimal RefundAmount { get; set; } = 30m;
}
