namespace GiftCardPlatform.Modules.Partners.Infrastructure;

internal sealed class PartnerMintRateWindow
{
    public Guid PartnerApiClientId { get; private set; }

    public DateTimeOffset WindowStartedAtUtc { get; private set; }

    public int RequestCount { get; private set; }
}
