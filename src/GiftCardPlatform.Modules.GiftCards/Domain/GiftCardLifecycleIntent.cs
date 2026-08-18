using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.GiftCards.Contracts;

namespace GiftCardPlatform.Modules.GiftCards.Domain;

internal sealed record GiftCardLifecycleIntent(
    GiftCardLifecycleAction Action,
    string Reason,
    string IdempotencyKey)
{
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;
    public const int IdempotencyKeyMinLength = 8;
    public const int IdempotencyKeyMaxLength = 128;
    public const string OwnerSuspendReason = "Cardholder self-service suspension.";
    public const string OwnerReactivateReason = "Cardholder self-service reactivation.";

    public static GiftCardLifecycleIntent CreateAdministrative(
        GiftCardLifecycleAction action,
        AdministerGiftCardLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new GiftCardLifecycleIntent(
            ValidateAction(action),
            NormalizeReason(request.Reason),
            NormalizeIdempotencyKey(request.IdempotencyKey));
    }

    public static GiftCardLifecycleIntent CreateOwner(
        GiftCardLifecycleAction action,
        OwnGiftCardLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (action is not GiftCardLifecycleAction.Suspend and
            not GiftCardLifecycleAction.Reactivate)
        {
            throw new ForbiddenException(
                "gift_card.lifecycle.owner_action.forbidden",
                "A cardholder may only suspend or reactivate an owned card.");
        }

        return new GiftCardLifecycleIntent(
            action,
            action == GiftCardLifecycleAction.Suspend
                ? OwnerSuspendReason
                : OwnerReactivateReason,
            NormalizeIdempotencyKey(request.IdempotencyKey));
    }

    public static GiftCardLifecycleIntent CreateSystemExpiration(Guid giftCardId)
    {
        if (giftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.required",
                "A gift card identifier is required.");
        }

        return new GiftCardLifecycleIntent(
            GiftCardLifecycleAction.Expire,
            "Automatic expiration at the configured card expiry.",
            $"gift-card-expiration-{giftCardId:N}");
    }

    public static string NormalizeIdempotencyKey(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < IdempotencyKeyMinLength or > IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.idempotency_key.invalid_length",
                $"Idempotency key must be between {IdempotencyKeyMinLength} and " +
                $"{IdempotencyKeyMaxLength} characters.");
        }

        return normalized;
    }

    private static GiftCardLifecycleAction ValidateAction(GiftCardLifecycleAction action)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.action.invalid",
                "The requested lifecycle action is invalid.");
        }

        return action;
    }

    private static string NormalizeReason(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < ReasonMinLength or > ReasonMaxLength)
        {
            throw new ValidationFailedException(
                "gift_card.lifecycle.reason.invalid_length",
                $"Reason must be between {ReasonMinLength} and {ReasonMaxLength} characters.");
        }

        return normalized;
    }
}
