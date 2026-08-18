using System.Globalization;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardInventoryQuery(
    GiftCardsDbContext dbContext,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator) : IGiftCardInventoryQuery
{
    public async Task<GiftCardInventoryPage> GetInventoryAsync(
        Guid organizationId,
        GiftCardInventoryRequest request,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.organization.required",
                "An inventory organization is required.");
        }

        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > GiftCardInventoryRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "gift_card.inventory.limit.invalid",
                $"Limit must be between 1 and {GiftCardInventoryRequest.MaxLimit}.");
        }

        var cursor = DecodeCursor(request.Cursor);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.GiftCardsView,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var query = dbContext.GiftCards
            .AsNoTracking()
            .Where(card =>
                card.OwnerOrganizationId == organizationId &&
                card.OwnershipState == GiftCardOwnershipState.OrganizationInventory);

        if (cursor is not null)
        {
            query = query.Where(card =>
                card.IssuedAtUtc < cursor.IssuedAtUtc ||
                (card.IssuedAtUtc == cursor.IssuedAtUtc &&
                 card.Id.CompareTo(cursor.Id) < 0));
        }

        var cards = await query
            .OrderByDescending(card => card.IssuedAtUtc)
            .ThenByDescending(card => card.Id)
            .Take(request.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = cards.Count > request.Limit;
        if (hasMore)
        {
            cards.RemoveAt(cards.Count - 1);
        }

        var nextCursor = hasMore && cards.Count > 0
            ? EncodeCursor(cards[^1].IssuedAtUtc, cards[^1].Id)
            : null;

        return new GiftCardInventoryPage(
            [.. cards.Select(GiftCardMapping.ToResult)],
            request.Limit,
            nextCursor);
    }

    private static string EncodeCursor(DateTimeOffset issuedAtUtc, Guid id)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{issuedAtUtc.UtcDateTime.Ticks}:{id:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static InventoryCursor? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - (normalized.Length % 4)) % 4),
                '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var parts = decoded.Split(':', StringSplitOptions.None);
            if (parts.Length != 2 ||
                !long.TryParse(
                    parts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var id) ||
                id == Guid.Empty)
            {
                throw new FormatException("Invalid cursor payload.");
            }

            return new InventoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ValidationFailedException(
                "gift_card.inventory.cursor.invalid",
                "The inventory cursor is invalid.");
        }
    }

    private sealed record InventoryCursor(DateTimeOffset IssuedAtUtc, Guid Id);
}
