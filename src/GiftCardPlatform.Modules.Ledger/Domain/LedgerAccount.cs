namespace GiftCardPlatform.Modules.Ledger.Domain;

internal enum LedgerAccountType
{
    PlatformFunding = 1,
    OrganizationCorporateCredit = 2,
    GiftCardValue = 3,
    PlatformRedemptionSettlement = 4,
}

internal sealed class LedgerAccount
{
    private LedgerAccount()
    {
        Currency = null!;
    }

    private LedgerAccount(
        Guid id,
        LedgerAccountType type,
        Guid? organizationId,
        Guid? giftCardId,
        string currency,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Type = type;
        OrganizationId = organizationId;
        GiftCardId = giftCardId;
        Currency = currency;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public LedgerAccountType Type { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public Guid? GiftCardId { get; private set; }

    public string Currency { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static LedgerAccount CreatePlatformFunding(string currency, DateTimeOffset createdAtUtc) =>
        new(
            Guid.CreateVersion7(),
            LedgerAccountType.PlatformFunding,
            organizationId: null,
            giftCardId: null,
            Money.Create(1m, currency).Currency,
            createdAtUtc.ToUniversalTime());

    public static LedgerAccount CreatePlatformRedemptionSettlement(
        string currency,
        DateTimeOffset createdAtUtc) =>
        new(
            Guid.CreateVersion7(),
            LedgerAccountType.PlatformRedemptionSettlement,
            organizationId: null,
            giftCardId: null,
            Money.Create(1m, currency).Currency,
            createdAtUtc.ToUniversalTime());

    public static LedgerAccount CreateOrganizationCorporateCredit(
        Guid organizationId,
        string currency,
        DateTimeOffset createdAtUtc)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        return new LedgerAccount(
            Guid.CreateVersion7(),
            LedgerAccountType.OrganizationCorporateCredit,
            organizationId,
            giftCardId: null,
            Money.Create(1m, currency).Currency,
            createdAtUtc.ToUniversalTime());
    }

    public static LedgerAccount CreateGiftCardValue(
        Guid fundingOrganizationId,
        Guid giftCardId,
        string currency,
        DateTimeOffset createdAtUtc)
    {
        if (fundingOrganizationId == Guid.Empty || giftCardId == Guid.Empty)
        {
            throw new ArgumentException(
                "A funding organization and gift card are required.",
                nameof(giftCardId));
        }

        return new LedgerAccount(
            Guid.CreateVersion7(),
            LedgerAccountType.GiftCardValue,
            fundingOrganizationId,
            giftCardId,
            Money.Create(1m, currency).Currency,
            createdAtUtc.ToUniversalTime());
    }

    public static LedgerAccount CreateGiftCardValue(
        Guid accountId,
        Guid fundingOrganizationId,
        Guid giftCardId,
        string currency,
        DateTimeOffset createdAtUtc)
    {
        if (accountId == Guid.Empty || fundingOrganizationId == Guid.Empty || giftCardId == Guid.Empty)
        {
            throw new ArgumentException(
                "An account, funding organization, and gift card are required.",
                nameof(giftCardId));
        }

        return new LedgerAccount(
            accountId,
            LedgerAccountType.GiftCardValue,
            fundingOrganizationId,
            giftCardId,
            Money.Create(1m, currency).Currency,
            createdAtUtc.ToUniversalTime());
    }
}
