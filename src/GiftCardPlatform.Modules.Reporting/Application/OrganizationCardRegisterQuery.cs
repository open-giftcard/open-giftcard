using System.Globalization;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace GiftCardPlatform.Modules.Reporting.Application;

/// <summary>
/// The organization's register of every card it funded (ADR-052).
///
/// Gift-card inventory answers "what have we not sent yet", because it filters
/// on organization ownership. Nothing answered "what have we issued", so a card
/// left the company's view at the moment it reached the person it was issued
/// for. This is that read.
///
/// Like the rest of Reporting it owns no schema and no projection: it composes
/// authoritative Gift Cards, Distribution and Ledger rows inside the normal
/// transaction and session-context boundary (ADR-036).
/// </summary>
internal sealed class OrganizationCardRegisterQuery(
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : IOrganizationCardRegisterQuery
{
    /// <summary>
    /// Balance is resolved for a card the company still owns and suppressed for
    /// one an identity owns. The decision is made here, in SQL, rather than by
    /// dropping a field after the fact, so a claimed card's remaining value is
    /// never read into memory in the first place.
    /// </summary>
    private const string RegisterSql = """
        select
            c.id,
            c.public_reference,
            c.lifecycle_state,
            c.ownership_state,
            c.initial_value,
            trim(c.currency),
            case
                when c.ownership_state = 'IdentityOwned' then null
                else coalesce((
                    select sum(
                        case when e.direction = 'Credit' then e.amount
                             else -e.amount end)
                    from ledger.entries e
                    where e.account_id = c.ledger_account_id), 0)
            end,
            c.issuing_organization_id,
            i.masked_recipient_contact,
            c.is_transferable,
            c.is_divisible,
            c.valid_from_utc,
            c.expires_at_utc,
            c.issued_at_utc,
            c.distributed_at_utc,
            c.claimed_at_utc
        from gift_cards.gift_cards c
        left join distribution.invitations i
            on i.id = c.distribution_invitation_id
        where c.funding_organization_id = @organization_id
          and (@lifecycle_state is null or c.lifecycle_state = @lifecycle_state)
          and (@ownership_state is null or c.ownership_state = @ownership_state)
          and (@currency is null or trim(c.currency) = @currency)
          and (@reference is null or c.public_reference ilike @reference escape '\')
          and (
                @cursor_issued_at is null
                or (c.issued_at_utc, c.id) < (@cursor_issued_at, @cursor_id)
              )
        order by c.issued_at_utc desc, c.id desc
        limit @limit
        """;

    public async Task<OrganizationCardRegisterPage> GetRegisterAsync(
        Guid organizationId,
        OrganizationCardRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var filters = OrganizationCardRegisterFilters.Create(request);
        var limit = NormalizeLimit(request.Limit);
        var cursor = ReportingCursorCodec.DecodeFiltered(
            request.Cursor,
            "reporting.card_register.cursor",
            filters.Fingerprint);

        await using var transaction = await BeginRegisterReadAsync(
            organizationId,
            cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            RegisterSql,
            transaction.Transaction.Connection
                ?? throw new InvalidOperationException(
                    "The reporting transaction has no open connection."),
            transaction.Transaction);

        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableText(command, "lifecycle_state", filters.LifecycleState);
        AddNullableText(command, "ownership_state", filters.OwnershipState);
        AddNullableText(command, "currency", filters.Currency);
        AddNullableText(command, "reference", filters.ReferencePattern);
        command.Parameters.Add(new NpgsqlParameter(
            "cursor_issued_at", NpgsqlDbType.TimestampTz)
        {
            Value = cursor is null
                ? DBNull.Value
                : cursor.OccurredAtUtc,
        });
        command.Parameters.Add(new NpgsqlParameter("cursor_id", NpgsqlDbType.Uuid)
        {
            Value = cursor is null
                ? DBNull.Value
                : Guid.Parse(cursor.StableKey),
        });

        // One more than asked for, so the presence of a next page is observed
        // rather than guessed at from a full page.
        command.Parameters.AddWithValue("limit", limit + 1);

        var items = new List<OrganizationCardRegisterItem>(limit + 1);
        await using (var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new OrganizationCardRegisterItem(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDecimal(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetBoolean(9),
                    reader.GetBoolean(10),
                    reader.GetFieldValue<DateTimeOffset>(11),
                    reader.GetFieldValue<DateTimeOffset>(12),
                    reader.GetFieldValue<DateTimeOffset>(13),
                    reader.IsDBNull(14)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(14),
                    reader.IsDBNull(15)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(15)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = ReportingCursorCodec.EncodeFiltered(
                last.IssuedAtUtc,
                last.GiftCardId.ToString("D", CultureInfo.InvariantCulture),
                filters.Fingerprint);
        }

        return new OrganizationCardRegisterPage(items, limit, nextCursor);
    }

    /// <summary>
    /// The register is a card read, not a financial one, so it requires the
    /// gift-card view permission alone. It deliberately does not require the
    /// corporate-credit permission that the financial reports pair with,
    /// because it discloses no corporate balance, and requiring it would force
    /// operators who only ever look up a card to hold finance authority.
    /// </summary>
    private async Task<IModuleTransaction> BeginRegisterReadAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "reporting.organization.required",
                "A tenant-root organization is required.");
        }

        var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (executionContext.IsPlatformOperator && !executionContext.IsSystem)
            {
                if (!executionContext.HasPlatformPermission(
                        PlatformPermissions.GiftCardsView))
                {
                    throw new ForbiddenException(
                        "reporting.platform_permissions.required",
                        "The gift-card view permission is required.");
                }

                return transaction;
            }

            if (executionContext.TenantRootOrganizationId != organizationId)
            {
                throw new ForbiddenException(
                    "reporting.scope.forbidden",
                    "The requested card register is not available.");
            }

            await organizationAuthorizer
                .RequirePermissionAsync(
                    organizationId,
                    OrganizationPermissions.GiftCardsView,
                    cancellationToken)
                .ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int NormalizeLimit(int limit) => limit switch
    {
        <= 0 => OrganizationCardRegisterRequest.DefaultLimit,
        > OrganizationCardRegisterRequest.MaxLimit =>
            OrganizationCardRegisterRequest.MaxLimit,
        _ => limit,
    };

    private static void AddNullableText(
        NpgsqlCommand command,
        string name,
        string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = (object?)value ?? DBNull.Value,
        });
}
