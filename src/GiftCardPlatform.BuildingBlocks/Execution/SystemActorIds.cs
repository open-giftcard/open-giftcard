namespace GiftCardPlatform.BuildingBlocks.Execution;

/// <summary>
/// Stable non-human actor identifiers used only for internal attribution.
/// They are not Identity users and never authenticate through HTTP.
/// </summary>
public static class SystemActorIds
{
    public static readonly Guid GiftCardExpiration =
        Guid.Parse("019c0598-6700-7000-8000-000000000014");

    public static readonly Guid ShareExpiration =
        Guid.Parse("019c0598-6700-7000-8000-000000000016");

    public static readonly Guid PaymentProvisionExpiration =
        Guid.Parse("019c0598-6700-7000-8000-000000000018");

    public static readonly Guid AuditCheckpoint =
        Guid.Parse("019c0598-6700-7000-8000-000000000020");

    public static readonly Guid NotificationDispatch =
        Guid.Parse("019c0598-6700-7000-8000-000000000022");

    public static readonly Guid BulkGiftCardBatch =
        Guid.Parse("019c0598-6700-7000-8000-000000000024");
}
