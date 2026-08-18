using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace GiftCardPlatform.Modules.Reporting.Application;

internal sealed class FinancialReportingQuery(
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IFinancialReportingQuery
{
    private const int MaximumReconciliationFindings = 500;

    public async Task<OrganizationFinancialSummary> GetOrganizationSummaryAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginFinancialReadAsync(
            organizationId,
            cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(SummarySql, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);

        var summaries = new List<OrganizationFinancialCurrencySummary>();
        await using (var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                summaries.Add(new OrganizationFinancialCurrencySummary(
                    reader.GetString(0),
                    reader.GetDecimal(1),
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetDecimal(7),
                    reader.GetDecimal(8),
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.GetDecimal(11)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OrganizationFinancialSummary(
            organizationId,
            timeProvider.GetUtcNow(),
            summaries);
    }

    public async Task<FinancialHistoryPage> GetOrganizationHistoryAsync(
        Guid organizationId,
        OrganizationFinancialHistoryRequest request,
        CancellationToken cancellationToken)
    {
        var pageRequest = new ReportingPageRequest(request.Limit, request.Cursor);
        ValidatePage(pageRequest, "reporting.history");
        var filters = FinancialHistorySearchFilters.Normalize(request);
        var cursor = filters.IsEmpty
            ? ReportingCursorCodec.Decode(request.Cursor, "reporting.history")
            : ReportingCursorCodec.DecodeFiltered(
                request.Cursor,
                "reporting.history",
                filters.Fingerprint);
        await using var transaction = await BeginFinancialReadAsync(
            organizationId,
            cancellationToken).ConfigureAwait(false);
        var page = await QueryHistoryAsync(
            OrganizationHistorySql,
            command => AddOrganizationHistoryParameters(
                command,
                organizationId,
                filters),
            pageRequest,
            cursor,
            transaction,
            cancellationToken,
            filters.IsEmpty ? null : filters.Fingerprint).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<OrganizationReconciliationResult> ReconcileOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginFinancialReadAsync(
            organizationId,
            cancellationToken).ConfigureAwait(false);

        var (transactionsChecked, giftCardsChecked, sharesChecked, activeReservationsChecked) =
            await GetReconciliationCountsAsync(
            organizationId,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var findings = await GetReconciliationFindingsAsync(
            organizationId,
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OrganizationReconciliationResult(
            organizationId,
            timeProvider.GetUtcNow(),
            findings.Count == 0,
            transactionsChecked,
            giftCardsChecked,
            sharesChecked,
            activeReservationsChecked,
            findings);
    }

    public async Task<OwnedGiftCardPage> GetMyGiftCardsAsync(
        ReportingPageRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePage(request, "reporting.my_cards");
        var userId = RequireCardholder();
        var cursor = ReportingCursorCodec.Decode(
            request.Cursor,
            "reporting.my_cards");
        Guid? cursorId = null;
        if (cursor is not null)
        {
            if (!Guid.TryParseExact(cursor.StableKey, "N", out var parsedId) ||
                parsedId == Guid.Empty)
            {
                throw new ValidationFailedException(
                    "reporting.my_cards.cursor.invalid",
                    "The reporting cursor is invalid.");
            }

            cursorId = parsedId;
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(OwnedGiftCardsSql, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset?>(
                "cursor_at",
                NpgsqlDbType.TimestampTz)
            {
                TypedValue = cursor?.OccurredAtUtc,
            });
        command.Parameters.Add(
            new NpgsqlParameter<Guid?>("cursor_id", NpgsqlDbType.Uuid)
            {
                TypedValue = cursorId,
            });
        command.Parameters.AddWithValue("take", request.Limit + 1);

        var cards = new List<OwnedGiftCardSummary>(request.Limit + 1);
        await using (var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cards.Add(new OwnedGiftCardSummary(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetString(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    GetNullableDateTimeOffset(reader, 10),
                    reader.GetFieldValue<DateTimeOffset>(11)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = cards.Count > request.Limit;
        if (hasMore)
        {
            cards.RemoveAt(cards.Count - 1);
        }

        var nextCursor = hasMore && cards.Count > 0
            ? ReportingCursorCodec.Encode(
                cards[^1].IssuedAtUtc,
                cards[^1].Id.ToString("N"))
            : null;
        return new OwnedGiftCardPage(cards, request.Limit, nextCursor);
    }

    public async Task<OwnedGiftCardDetail> GetMyGiftCardAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        EnsureGiftCardId(giftCardId);
        var userId = RequireCardholder();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        var detail = await GetOwnedGiftCardAsync(
            userId,
            giftCardId,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return detail;
    }

    public async Task<FinancialHistoryPage> GetMyGiftCardHistoryAsync(
        Guid giftCardId,
        ReportingPageRequest request,
        CancellationToken cancellationToken)
    {
        EnsureGiftCardId(giftCardId);
        ValidatePage(request, "reporting.gift_card_history");
        var userId = RequireCardholder();
        var cursor = ReportingCursorCodec.Decode(
            request.Cursor,
            "reporting.gift_card_history");

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await GetOwnedGiftCardAsync(
            userId,
            giftCardId,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var page = await QueryHistoryAsync(
            OwnedGiftCardHistorySql,
            command =>
            {
                command.Parameters.AddWithValue("user_id", userId);
                command.Parameters.AddWithValue("gift_card_id", giftCardId);
            },
            request,
            cursor,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    private async Task<IModuleTransaction> BeginFinancialReadAsync(
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
            if (executionContext.IsPlatformOperator &&
                !executionContext.IsSystem)
            {
                if (!executionContext.HasPlatformPermission(
                        PlatformPermissions.CorporateCreditsView) ||
                    !executionContext.HasPlatformPermission(
                        PlatformPermissions.GiftCardsView))
                {
                    throw new ForbiddenException(
                        "reporting.platform_permissions.required",
                        "Corporate-credit and gift-card view permissions are required.");
                }

                return transaction;
            }

            if (executionContext.TenantRootOrganizationId != organizationId)
            {
                throw new ForbiddenException(
                    "reporting.scope.forbidden",
                    "The requested financial scope is not available.");
            }

            await organizationAuthorizer
                .RequirePermissionAsync(
                    organizationId,
                    OrganizationPermissions.CorporateCreditsView,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private static async Task<OwnedGiftCardDetail> GetOwnedGiftCardAsync(
        Guid userId,
        Guid giftCardId,
        IModuleTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(OwnedGiftCardDetailSql, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("gift_card_id", giftCardId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "gift_card.not_found",
                "Gift card not found.");
        }

        return new OwnedGiftCardDetail(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8),
            reader.GetDecimal(9),
            reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetBoolean(13),
            reader.GetBoolean(14),
            reader.GetGuid(15),
            reader.GetInt32(16),
            GetNullableGuid(reader, 17),
            GetNullableDateTimeOffset(reader, 18),
            GetNullableDateTimeOffset(reader, 19),
            reader.GetFieldValue<DateTimeOffset>(20));
    }

    private static async Task<FinancialHistoryPage> QueryHistoryAsync(
        string sql,
        Action<NpgsqlCommand> addScopeParameters,
        ReportingPageRequest request,
        ReportingCursor? cursor,
        IModuleTransaction transaction,
        CancellationToken cancellationToken,
        string? filterFingerprint = null)
    {
        await using var command = CreateCommand(sql, transaction);
        addScopeParameters(command);
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset?>(
                "cursor_at",
                NpgsqlDbType.TimestampTz)
            {
                TypedValue = cursor?.OccurredAtUtc,
            });
        command.Parameters.Add(
            new NpgsqlParameter<string?>("cursor_key", NpgsqlDbType.Text)
            {
                TypedValue = cursor?.StableKey,
            });
        command.Parameters.AddWithValue("take", request.Limit + 1);

        var rows = new List<FinancialHistoryItem>(request.Limit + 1);
        await using (var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FinancialHistoryItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetGuid(3),
                    GetNullableGuid(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    GetNullableDecimal(reader, 7),
                    GetNullableString(reader, 8),
                    reader.GetString(9),
                    GetNullableString(reader, 10),
                    GetNullableGuid(reader, 11),
                    reader.GetFieldValue<DateTimeOffset>(12)));
            }
        }

        var hasMore = rows.Count > request.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var nextCursor = hasMore && rows.Count > 0
            ? filterFingerprint is null
                ? ReportingCursorCodec.Encode(
                    rows[^1].OccurredAtUtc,
                    rows[^1].EventKey)
                : ReportingCursorCodec.EncodeFiltered(
                    rows[^1].OccurredAtUtc,
                    rows[^1].EventKey,
                    filterFingerprint)
            : null;
        return new FinancialHistoryPage(rows, request.Limit, nextCursor);
    }

    private static void AddOrganizationHistoryParameters(
        NpgsqlCommand command,
        Guid organizationId,
        FinancialHistorySearchFilters filters)
    {
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.Add(
            new NpgsqlParameter<string?>("category", NpgsqlDbType.Text)
            {
                TypedValue = filters.Category,
            });
        command.Parameters.Add(
            new NpgsqlParameter<string?>("operation", NpgsqlDbType.Text)
            {
                TypedValue = filters.Operation,
            });
        command.Parameters.Add(
            new NpgsqlParameter<string?>("currency", NpgsqlDbType.Text)
            {
                TypedValue = filters.Currency,
            });
        command.Parameters.Add(
            new NpgsqlParameter<string?>("reference_pattern", NpgsqlDbType.Text)
            {
                TypedValue = filters.ReferencePattern,
            });
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset?>(
                "occurred_from_utc",
                NpgsqlDbType.TimestampTz)
            {
                TypedValue = filters.OccurredFromUtc,
            });
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset?>(
                "occurred_before_utc",
                NpgsqlDbType.TimestampTz)
            {
                TypedValue = filters.OccurredBeforeUtc,
            });
    }

    private static async Task<(int Transactions, int Cards, int Shares, int ActiveReservations)>
        GetReconciliationCountsAsync(
        Guid organizationId,
        IModuleTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(ReconciliationCountsSql, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private static async Task<IReadOnlyList<ReconciliationFinding>>
        GetReconciliationFindingsAsync(
            Guid organizationId,
            IModuleTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(ReconciliationFindingsSql, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("take", MaximumReconciliationFindings + 1);
        var findings = new List<ReconciliationFinding>();
        await using (var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings.Add(new ReconciliationFinding(
                    reader.GetString(0),
                    Enum.Parse<ReconciliationSeverity>(
                        reader.GetString(1),
                        ignoreCase: false),
                    reader.GetString(2),
                    reader.GetString(3),
                    GetNullableString(reader, 4),
                    GetNullableDecimal(reader, 5),
                    GetNullableDecimal(reader, 6),
                    reader.GetString(7)));
            }
        }

        if (findings.Count > MaximumReconciliationFindings)
        {
            findings.RemoveRange(
                MaximumReconciliationFindings,
                findings.Count - MaximumReconciliationFindings);
            findings.Add(new ReconciliationFinding(
                "reconciliation.findings.truncated",
                ReconciliationSeverity.Warning,
                "Organization",
                organizationId.ToString(),
                null,
                null,
                null,
                $"Only the first {MaximumReconciliationFindings} findings are returned."));
        }

        return findings;
    }

    private Guid RequireCardholder()
    {
        if (!executionContext.IsAuthenticated ||
            executionContext.IsPlatformOperator ||
            executionContext.IsSystem ||
            executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "reporting.cardholder.required",
                "An authenticated cardholder is required.");
        }

        return executionContext.UserId.Value;
    }

    private static void EnsureGiftCardId(Guid giftCardId)
    {
        if (giftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.required",
                "A gift card identifier is required.");
        }
    }

    private static void ValidatePage(
        ReportingPageRequest request,
        string errorPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > ReportingPageRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.limit.invalid",
                $"Limit must be between 1 and {ReportingPageRequest.MaxLimit}.");
        }
    }

    private static NpgsqlCommand CreateCommand(
        string sql,
        IModuleTransaction transaction) =>
        new(
            sql,
            transaction.Transaction.Connection
                ?? throw new InvalidOperationException(
                    "The reporting transaction has no open connection."),
            transaction.Transaction);

    private static Guid? GetNullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? GetNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private const string SummarySql =
        """
        with currencies as (
            select currency
            from corporate_credits.allocations
            where organization_id = @organization_id
            union
            select currency
            from corporate_credits.reversals
            where organization_id = @organization_id
            union
            select currency
            from gift_cards.gift_cards
            where funding_organization_id = @organization_id
            union
            select currency
            from ledger.accounts
            where organization_id = @organization_id
        ),
        granted as (
            select currency, sum(amount) as amount
            from corporate_credits.allocations
            where organization_id = @organization_id
            group by currency
        ),
        reversed as (
            select currency, sum(amount) as amount
            from corporate_credits.reversals
            where organization_id = @organization_id
            group by currency
        ),
        issued as (
            select
                currency,
                sum(initial_value) as amount,
                sum(initial_value) filter (
                    where distributed_at_utc is not null
                ) as distributed_amount
            from gift_cards.gift_cards
            where funding_organization_id = @organization_id
              and source_gift_card_id is null
            group by currency
        ),
        corporate_balance as (
            select
                account.currency,
                sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
            from ledger.accounts account
            left join ledger.entries entry on entry.account_id = account.id
            where account.organization_id = @organization_id
              and account.type = 'OrganizationCorporateCredit'
            group by account.currency
        ),
        card_balance as (
            select
                account.currency,
                sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
            from ledger.accounts account
            left join ledger.entries entry on entry.account_id = account.id
            where account.organization_id = @organization_id
              and account.type = 'GiftCardValue'
            group by account.currency
        ),
        returned as (
            select
                currency,
                sum(returned_amount) filter (where action = 'Cancel')
                    as cancelled_amount,
                sum(returned_amount) filter (where action = 'Expire')
                    as expired_amount
            from gift_cards.lifecycle_events
            where funding_organization_id = @organization_id
              and action in ('Cancel', 'Expire')
            group by currency
        ),
        spent as (
            select entry.currency, sum(entry.amount) as amount
            from ledger.transactions ledger_transaction
            join ledger.entries entry on entry.transaction_id = ledger_transaction.id
            join ledger.accounts account on account.id = entry.account_id
            where ledger_transaction.organization_id = @organization_id
              and ledger_transaction.operation_type = 'gift_card.redemption'
              and account.type = 'GiftCardValue'
              and entry.direction = 'Debit'
            group by entry.currency
        ),
        refunded as (
            select currency, sum(amount) as amount
            from payments.payment_refunds
            where funding_organization_id = @organization_id
            group by currency
        )
        select
            currency.currency,
            coalesce(granted.amount, 0),
            coalesce(reversed.amount, 0),
            coalesce(issued.amount, 0),
            coalesce(issued.distributed_amount, 0),
            coalesce(corporate_balance.amount, 0),
            coalesce(card_balance.amount, 0),
            coalesce(returned.cancelled_amount, 0),
            coalesce(returned.expired_amount, 0),
            coalesce(spent.amount, 0),
            coalesce(refunded.amount, 0),
            coalesce(spent.amount, 0) - coalesce(refunded.amount, 0)
        from currencies currency
        left join granted using (currency)
        left join reversed using (currency)
        left join issued using (currency)
        left join corporate_balance using (currency)
        left join card_balance using (currency)
        left join returned using (currency)
        left join spent using (currency)
        left join refunded using (currency)
        order by currency.currency
        """;

    private const string OrganizationHistorySql =
        """
        with history as (
            select
                'allocation:' || allocation.id::text as event_key,
                'CorporateCredit'::text as category,
                'Allocated'::text as operation,
                allocation.id as entity_id,
                null::uuid as gift_card_id,
                null::text as public_reference,
                allocation.business_reference,
                allocation.amount,
                allocation.currency,
                'Credit'::text as financial_direction,
                null::text as state,
                allocation.allocated_by_user_id as actor_user_id,
                allocation.allocated_at_utc as occurred_at_utc
            from corporate_credits.allocations allocation
            where allocation.organization_id = @organization_id

            union all

            select
                'reversal:' || reversal.id::text,
                'CorporateCredit',
                'Reversed',
                reversal.id,
                null::uuid,
                null::text,
                reversal.reason,
                reversal.amount,
                reversal.currency,
                'Debit',
                null::text,
                reversal.reversed_by_user_id,
                reversal.reversed_at_utc
            from corporate_credits.reversals reversal
            where reversal.organization_id = @organization_id

            union all

            select
                'gift-card:' || card.id::text || ':issued',
                'GiftCard',
                'Issued',
                card.id,
                card.id,
                card.public_reference,
                card.business_reference,
                card.initial_value,
                card.currency,
                'Debit',
                card.lifecycle_state,
                card.issued_by_user_id,
                card.issued_at_utc
            from gift_cards.gift_cards card
            where card.funding_organization_id = @organization_id
              and card.source_gift_card_id is null

            union all

            select
                'distribution:' || event.id::text,
                'Distribution',
                event.event_type,
                event.id,
                event.gift_card_id,
                card.public_reference,
                invitation.business_reference,
                null::numeric,
                card.currency,
                'None',
                invitation.state,
                event.actor_user_id,
                event.occurred_at_utc
            from distribution.events event
            join distribution.invitations invitation
              on invitation.id = event.invitation_id
            join gift_cards.gift_cards card
              on card.id = event.gift_card_id
            where event.funding_organization_id = @organization_id

            union all

            select
                'lifecycle:' || lifecycle.id::text,
                'Lifecycle',
                lifecycle.action,
                lifecycle.id,
                lifecycle.gift_card_id,
                card.public_reference,
                lifecycle.reason,
                lifecycle.returned_amount,
                coalesce(lifecycle.currency, card.currency),
                case
                    when lifecycle.returned_amount is not null then 'Credit'
                    else 'None'
                end,
                lifecycle.new_state,
                lifecycle.actor_user_id,
                lifecycle.occurred_at_utc
            from gift_cards.lifecycle_events lifecycle
            join gift_cards.gift_cards card
              on card.id = lifecycle.gift_card_id
            where lifecycle.funding_organization_id = @organization_id

            union all

            select
                'sharing:' || event.id::text,
                'Sharing',
                event.event_type,
                share.id,
                share.source_gift_card_id,
                source.public_reference,
                case
                    when share.masked_recipient_contact is not null
                        then share.kind || ':' || share.masked_recipient_contact
                    else share.kind
                end,
                share.amount,
                share.currency,
                case event.event_type
                    when 'Created' then 'Reserved'
                    when 'Claimed' then 'Transferred'
                    when 'Cancelled' then 'Released'
                    when 'Expired' then 'Released'
                    when 'Locked' then 'Released'
                    else 'None'
                end,
                share.state,
                event.actor_user_id,
                event.occurred_at_utc
            from sharing.events event
            join sharing.shares share on share.id = event.share_id
            join gift_cards.gift_cards source on source.id = share.source_gift_card_id
            where event.funding_organization_id = @organization_id

            union all

            select
                'redemption:' || provision.id::text,
                'Redemption',
                'Confirmed',
                provision.id,
                provision.gift_card_id,
                provision.gift_card_public_reference,
                coalesce(
                    provision.pos_transaction_reference,
                    'PAYMENT-' || replace(provision.id::text, '-', '')
                ),
                provision.confirmed_amount,
                provision.currency,
                'Spent',
                provision.state,
                provision.pos_client_id,
                provision.settled_at_utc
            from payments.payment_provisions provision
            where provision.funding_organization_id = @organization_id
              and provision.state = 'Confirmed'

            union all

            select
                'refund:' || refund.id::text,
                'Refund',
                'Refunded',
                refund.id,
                refund.gift_card_id,
                refund.gift_card_public_reference,
                coalesce(
                    refund.pos_transaction_reference,
                    'REFUND-' || replace(refund.id::text, '-', '')
                ),
                refund.amount,
                refund.currency,
                'Refunded',
                'Completed',
                refund.pos_client_id,
                refund.refunded_at_utc
            from payments.payment_refunds refund
            where refund.funding_organization_id = @organization_id
        )
        select
            event_key,
            category,
            operation,
            entity_id,
            gift_card_id,
            public_reference,
            business_reference,
            amount,
            currency,
            financial_direction,
            state,
            actor_user_id,
            occurred_at_utc
        from history
        where (@category is null or lower(category) = @category)
          and (@operation is null or lower(operation) = @operation)
          and (@currency is null or currency = @currency)
          and (
              @reference_pattern is null
              or public_reference ilike @reference_pattern escape '\'
              or business_reference ilike @reference_pattern escape '\'
          )
          and (@occurred_from_utc is null or occurred_at_utc >= @occurred_from_utc)
          and (@occurred_before_utc is null or occurred_at_utc < @occurred_before_utc)
          and (
              @cursor_at is null
              or occurred_at_utc < @cursor_at
              or (
                  occurred_at_utc = @cursor_at
                  and event_key < @cursor_key
              )
          )
        order by occurred_at_utc desc, event_key desc
        limit @take
        """;

    private const string OwnedGiftCardsSql =
        """
        select
            card.id,
            card.public_reference,
            card.lifecycle_state,
            card.initial_value,
            coalesce(balance.amount, 0),
            coalesce(reservation.amount, 0),
            coalesce(balance.amount, 0) - coalesce(reservation.amount, 0),
            card.currency,
            card.valid_from_utc,
            card.expires_at_utc,
            card.claimed_at_utc,
            card.issued_at_utc
        from gift_cards.gift_cards card
        left join lateral (
            select sum(
                case entry.direction
                    when 'Credit' then entry.amount
                    else -entry.amount
                end
            ) as amount
            from ledger.entries entry
            where entry.account_id = card.ledger_account_id
        ) balance on true
        left join lateral (
            -- Reserved value is every active hold on the card, of either kind:
            -- pending share reservations and active payment provisions. Counting
            -- only one would show a cardholder value they cannot spend
            -- (ADR-015, ADR-033).
            select coalesce(shared.amount, 0) + coalesce(provisioned.amount, 0) as amount
            from (
                select sum(share.amount) as amount
                from sharing.shares share
                where share.source_gift_card_id = card.id
                  and share.state in ('Pending', 'Claiming')
            ) shared
            cross join (
                select sum(provision.amount) as amount
                from payments.payment_provisions provision
                where provision.gift_card_id = card.id
                  and provision.state = 'Active'
                  and provision.expires_at_utc > now()
            ) provisioned
        ) reservation on true
        where card.owner_user_id = @user_id
          and card.ownership_state = 'IdentityOwned'
          and (
              @cursor_at is null
              or card.issued_at_utc < @cursor_at
              or (
                  card.issued_at_utc = @cursor_at
                  and card.id < @cursor_id
              )
          )
        order by card.issued_at_utc desc, card.id desc
        limit @take
        """;

    private const string OwnedGiftCardDetailSql =
        """
        select
            card.id,
            card.public_reference,
            card.funding_organization_id,
            card.issuing_organization_id,
            card.ownership_state,
            card.lifecycle_state,
            card.initial_value,
            coalesce(balance.amount, 0),
            coalesce(reservation.amount, 0),
            coalesce(balance.amount, 0) - coalesce(reservation.amount, 0),
            card.currency,
            card.valid_from_utc,
            card.expires_at_utc,
            card.is_transferable,
            card.is_divisible,
            card.root_gift_card_id,
            card.generation,
            card.distribution_invitation_id,
            card.distributed_at_utc,
            card.claimed_at_utc,
            card.issued_at_utc
        from gift_cards.gift_cards card
        left join lateral (
            select sum(
                case entry.direction
                    when 'Credit' then entry.amount
                    else -entry.amount
                end
            ) as amount
            from ledger.entries entry
            where entry.account_id = card.ledger_account_id
        ) balance on true
        left join lateral (
            -- Reserved value is every active hold on the card, of either kind:
            -- pending share reservations and active payment provisions. Counting
            -- only one would show a cardholder value they cannot spend
            -- (ADR-015, ADR-033).
            select coalesce(shared.amount, 0) + coalesce(provisioned.amount, 0) as amount
            from (
                select sum(share.amount) as amount
                from sharing.shares share
                where share.source_gift_card_id = card.id
                  and share.state in ('Pending', 'Claiming')
            ) shared
            cross join (
                select sum(provision.amount) as amount
                from payments.payment_provisions provision
                where provision.gift_card_id = card.id
                  and provision.state = 'Active'
                  and provision.expires_at_utc > now()
            ) provisioned
        ) reservation on true
        where card.id = @gift_card_id
          and card.owner_user_id = @user_id
          and card.ownership_state = 'IdentityOwned'
        """;

    private const string OwnedGiftCardHistorySql =
        """
        with history as (
            select
                'ledger:' || ledger_transaction.id::text as event_key,
                'Ledger'::text as category,
                ledger_transaction.operation_type as operation,
                ledger_transaction.id as entity_id,
                card.id as gift_card_id,
                card.public_reference,
                ledger_transaction.business_reference,
                abs(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                )) as amount,
                entry.currency,
                case
                    when sum(
                        case entry.direction
                            when 'Credit' then entry.amount
                            else -entry.amount
                        end
                    ) >= 0 then 'Credit'
                    else 'Debit'
                end as financial_direction,
                card.lifecycle_state as state,
                ledger_transaction.initiated_by_user_id as actor_user_id,
                ledger_transaction.posted_at_utc as occurred_at_utc
            from gift_cards.gift_cards card
            join ledger.entries entry
              on entry.account_id = card.ledger_account_id
            join ledger.transactions ledger_transaction
              on ledger_transaction.id = entry.transaction_id
            where card.id = @gift_card_id
              and card.owner_user_id = @user_id
              and card.ownership_state = 'IdentityOwned'
            group by
                ledger_transaction.id,
                card.id,
                card.public_reference,
                card.lifecycle_state,
                entry.currency

            union all

            select
                'distribution:' || event.id::text,
                'Distribution',
                event.event_type,
                event.id,
                card.id,
                card.public_reference,
                invitation.business_reference,
                null::numeric,
                card.currency,
                'None',
                invitation.state,
                event.actor_user_id,
                event.occurred_at_utc
            from gift_cards.gift_cards card
            join distribution.events event on event.gift_card_id = card.id
            join distribution.invitations invitation
              on invitation.id = event.invitation_id
            where card.id = @gift_card_id
              and card.owner_user_id = @user_id
              and card.ownership_state = 'IdentityOwned'

            union all

            select
                'lifecycle:' || lifecycle.id::text,
                'Lifecycle',
                lifecycle.action,
                lifecycle.id,
                card.id,
                card.public_reference,
                lifecycle.reason,
                lifecycle.returned_amount,
                coalesce(lifecycle.currency, card.currency),
                case
                    when lifecycle.returned_amount is not null then 'Debit'
                    else 'None'
                end,
                lifecycle.new_state,
                lifecycle.actor_user_id,
                lifecycle.occurred_at_utc
            from gift_cards.gift_cards card
            join gift_cards.lifecycle_events lifecycle
              on lifecycle.gift_card_id = card.id
            where card.id = @gift_card_id
              and card.owner_user_id = @user_id
              and card.ownership_state = 'IdentityOwned'

            union all

            select
                'sharing:' || event.id::text,
                'Sharing',
                event.event_type,
                share.id,
                card.id,
                card.public_reference,
                case
                    when share.masked_recipient_contact is not null
                        then share.kind || ':' || share.masked_recipient_contact
                    else share.kind
                end,
                share.amount,
                share.currency,
                case
                    when share.child_gift_card_id = card.id then 'Credit'
                    when event.event_type = 'Created' then 'Reserved'
                    when event.event_type = 'Claimed' then 'Debit'
                    when event.event_type in ('Cancelled', 'Expired', 'Locked') then 'Released'
                    else 'None'
                end,
                share.state,
                event.actor_user_id,
                event.occurred_at_utc
            from gift_cards.gift_cards card
            join sharing.shares share
              on share.source_gift_card_id = card.id
              or share.child_gift_card_id = card.id
            join sharing.events event on event.share_id = share.id
            where card.id = @gift_card_id
              and card.owner_user_id = @user_id
              and card.ownership_state = 'IdentityOwned'
              and (
                  share.source_gift_card_id = card.id
                  or event.event_type = 'Claimed'
              )
        )
        select
            event_key,
            category,
            operation,
            entity_id,
            gift_card_id,
            public_reference,
            business_reference,
            amount,
            currency,
            financial_direction,
            state,
            actor_user_id,
            occurred_at_utc
        from history
        where @cursor_at is null
           or occurred_at_utc < @cursor_at
           or (
                occurred_at_utc = @cursor_at
                and event_key < @cursor_key
           )
        order by occurred_at_utc desc, event_key desc
        limit @take
        """;

    private const string ReconciliationCountsSql =
        """
        select
            (
                select count(*)::int
                from ledger.transactions
                where organization_id = @organization_id
                  and operation_type in (
                      'corporate_credit.allocation',
                      'corporate_credit.reversal',
                      'gift_card.issuance',
                      'gift_card.cancellation_return',
                      'gift_card.expiration_return',
                      'gift_card.share_transfer',
                      'gift_card.redemption',
                      'gift_card.refund'
                  )
            ),
            (
                select count(*)::int
                from gift_cards.gift_cards
                where funding_organization_id = @organization_id
            ),
            (
                select count(*)::int
                from sharing.shares
                where funding_organization_id = @organization_id
            ),
            (
                select (
                    (select count(*) from sharing.shares
                     where funding_organization_id = @organization_id
                       and state in ('Pending', 'Claiming'))
                    +
                    (select count(*) from payments.payment_provisions
                     where funding_organization_id = @organization_id
                       and state = 'Active'
                       and expires_at_utc > now())
                )::int
            )
        """;

    private const string ReconciliationFindingsSql =
        """
        with relevant_transactions as (
            select ledger_transaction.*
            from ledger.transactions ledger_transaction
            where ledger_transaction.organization_id = @organization_id
              and ledger_transaction.operation_type in (
                  'corporate_credit.allocation',
                  'corporate_credit.reversal',
                  'gift_card.issuance',
                  'gift_card.cancellation_return',
                  'gift_card.expiration_return',
                  'gift_card.share_transfer',
                  'gift_card.redemption',
                  'gift_card.refund'
              )
        ),
        transaction_totals as (
            select
                ledger_transaction.id,
                entry.currency,
                count(entry.id) as entry_count,
                coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as net_amount
            from relevant_transactions ledger_transaction
            left join ledger.entries entry
              on entry.transaction_id = ledger_transaction.id
            group by ledger_transaction.id, entry.currency
        ),
        corporate_expected as (
            select currency, sum(amount) as amount
            from corporate_credits.allocations
            where organization_id = @organization_id
            group by currency
            union all
            select currency, -sum(amount)
            from corporate_credits.reversals
            where organization_id = @organization_id
            group by currency
            union all
            select currency, -sum(initial_value)
            from gift_cards.gift_cards
            where funding_organization_id = @organization_id
              and source_gift_card_id is null
            group by currency
            union all
            select currency, sum(returned_amount)
            from gift_cards.lifecycle_events
            where funding_organization_id = @organization_id
              and action in ('Cancel', 'Expire')
            group by currency
        ),
        corporate_expected_totals as (
            select currency, sum(amount) as amount
            from corporate_expected
            group by currency
        ),
        corporate_actual as (
            select
                account.currency,
                coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as amount
            from ledger.accounts account
            left join ledger.entries entry on entry.account_id = account.id
            where account.organization_id = @organization_id
              and account.type = 'OrganizationCorporateCredit'
            group by account.currency
        ),
        settlement_movements as (
            select currency, sum(confirmed_amount) as amount
            from payments.payment_provisions
            where state = 'Confirmed'
            group by currency
            union all
            select currency, -sum(amount)
            from payments.payment_refunds
            group by currency
        ),
        settlement_expected as (
            select currency, sum(amount) as amount
            from settlement_movements
            group by currency
        ),
        settlement_actual as (
            select
                account.currency,
                coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as amount
            from ledger.accounts account
            left join ledger.entries entry on entry.account_id = account.id
            where account.type = 'PlatformRedemptionSettlement'
            group by account.currency
        ),
        findings as (
            select
                case
                    when total.entry_count < 2
                        then 'ledger.transaction.entry_count'
                    else 'ledger.transaction.unbalanced'
                end as code,
                'Error'::text as severity,
                'LedgerTransaction'::text as entity_type,
                total.id::text as entity_id,
                total.currency,
                0::numeric as expected_amount,
                total.net_amount as actual_amount,
                case
                    when total.entry_count < 2
                        then 'A posted transaction does not contain at least two entries.'
                    else 'A posted transaction is not balanced for its currency.'
                end as message
            from transaction_totals total
            where total.entry_count < 2 or total.net_amount <> 0

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'corporate_credit.allocation.transaction_missing'
                    when ledger_transaction.operation_type <> 'corporate_credit.allocation'
                        then 'corporate_credit.allocation.operation_mismatch'
                    else 'corporate_credit.allocation.amount_mismatch'
                end,
                'Error',
                'CorporateCreditAllocation',
                allocation.id::text,
                allocation.currency,
                allocation.amount,
                coalesce(effect.amount, 0),
                'Allocation domain data does not match its corporate-credit Ledger effect.'
            from corporate_credits.allocations allocation
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = allocation.ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                join ledger.accounts account on account.id = entry.account_id
                where entry.transaction_id = allocation.ledger_transaction_id
                  and account.type = 'OrganizationCorporateCredit'
                  and account.organization_id = @organization_id
                  and entry.currency = allocation.currency
            ) effect on true
            where allocation.organization_id = @organization_id
              and (
                  ledger_transaction.id is null
                  or ledger_transaction.operation_type <> 'corporate_credit.allocation'
                  or coalesce(effect.amount, 0) <> allocation.amount
              )

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'corporate_credit.reversal.transaction_missing'
                    when ledger_transaction.operation_type <> 'corporate_credit.reversal'
                        then 'corporate_credit.reversal.operation_mismatch'
                    else 'corporate_credit.reversal.amount_mismatch'
                end,
                'Error',
                'CorporateCreditReversal',
                reversal.id::text,
                reversal.currency,
                -reversal.amount,
                coalesce(effect.amount, 0),
                'Reversal domain data does not match its corporate-credit Ledger effect.'
            from corporate_credits.reversals reversal
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = reversal.ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                join ledger.accounts account on account.id = entry.account_id
                where entry.transaction_id = reversal.ledger_transaction_id
                  and account.type = 'OrganizationCorporateCredit'
                  and account.organization_id = @organization_id
                  and entry.currency = reversal.currency
            ) effect on true
            where reversal.organization_id = @organization_id
              and (
                  ledger_transaction.id is null
                  or ledger_transaction.operation_type <> 'corporate_credit.reversal'
                  or coalesce(effect.amount, 0) <> -reversal.amount
              )

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'gift_card.issuance.transaction_missing'
                    when ledger_transaction.operation_type <> 'gift_card.issuance'
                        then 'gift_card.issuance.operation_mismatch'
                    else 'gift_card.issuance.amount_mismatch'
                end,
                'Error',
                'GiftCard',
                card.id::text,
                card.currency,
                card.initial_value,
                coalesce(effect.amount, 0),
                'Gift-card funding data does not match its issuance Ledger effect.'
            from gift_cards.gift_cards card
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = card.issuance_ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                where entry.transaction_id = card.issuance_ledger_transaction_id
                  and entry.account_id = card.ledger_account_id
                  and entry.currency = card.currency
            ) effect on true
            where card.funding_organization_id = @organization_id
              and card.source_gift_card_id is null
              and (
                  ledger_transaction.id is null
                  or ledger_transaction.operation_type <> 'gift_card.issuance'
                  or coalesce(effect.amount, 0) <> card.initial_value
              )

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'gift_card.return.transaction_missing'
                    when ledger_transaction.operation_type <> case lifecycle.action
                        when 'Cancel' then 'gift_card.cancellation_return'
                        else 'gift_card.expiration_return'
                    end
                        then 'gift_card.return.operation_mismatch'
                    else 'gift_card.return.amount_mismatch'
                end,
                'Error',
                'GiftCardLifecycleEvent',
                lifecycle.id::text,
                lifecycle.currency,
                -lifecycle.returned_amount,
                coalesce(effect.amount, 0),
                'Terminal lifecycle data does not match its gift-card Ledger effect.'
            from gift_cards.lifecycle_events lifecycle
            join gift_cards.gift_cards card
              on card.id = lifecycle.gift_card_id
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = lifecycle.ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                where entry.transaction_id = lifecycle.ledger_transaction_id
                  and entry.account_id = card.ledger_account_id
                  and entry.currency = lifecycle.currency
            ) effect on true
            where lifecycle.funding_organization_id = @organization_id
              and lifecycle.action in ('Cancel', 'Expire')
              and lifecycle.returned_amount > 0
              and (
                  ledger_transaction.id is null
                  or ledger_transaction.operation_type <> case lifecycle.action
                      when 'Cancel' then 'gift_card.cancellation_return'
                      else 'gift_card.expiration_return'
                  end
                  or coalesce(effect.amount, 0) <> -lifecycle.returned_amount
              )

            union all

            select
                case
                    when card.lifecycle_state in ('Cancelled', 'Expired')
                        then 'gift_card.terminal_balance.nonzero'
                    when balance.amount < 0
                        then 'gift_card.balance.negative'
                    else 'gift_card.balance.exceeds_funding'
                end,
                'Error',
                'GiftCard',
                card.id::text,
                card.currency,
                case
                    when card.lifecycle_state in ('Cancelled', 'Expired') then 0
                    else card.initial_value
                end,
                balance.amount,
                'The current gift-card Ledger balance is inconsistent with its lifecycle or funding.'
            from gift_cards.gift_cards card
            join lateral (
                select coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as amount
                from ledger.entries entry
                where entry.account_id = card.ledger_account_id
            ) balance on true
            where card.funding_organization_id = @organization_id
              and (
                  (
                      card.lifecycle_state in ('Cancelled', 'Expired')
                      and balance.amount <> 0
                  )
                  or balance.amount < 0
                  or balance.amount > card.initial_value
              )

            union all

            select
                'organization.corporate_balance.mismatch',
                'Error',
                'Organization',
                @organization_id::text,
                coalesce(expected.currency, actual.currency),
                coalesce(expected.amount, 0),
                coalesce(actual.amount, 0),
                'The corporate-credit Ledger balance does not match rebuildable domain totals.'
            from corporate_expected_totals expected
            full join corporate_actual actual using (currency)
            where coalesce(expected.amount, 0) <> coalesce(actual.amount, 0)

            union all

            select
                'sharing.claim.incomplete',
                'Error',
                'GiftCardShare',
                share.id::text,
                share.currency,
                share.amount,
                null::numeric,
                'A share remains in the transaction-only Claiming state.'
            from sharing.shares share
            where share.funding_organization_id = @organization_id
              and share.state = 'Claiming'

            union all

            select
                'sharing.reservation.source_invalid',
                'Error',
                'GiftCardShare',
                share.id::text,
                share.currency,
                share.amount,
                balance.amount,
                'An active reservation is attached to an ineligible or differently owned source card.'
            from sharing.shares share
            left join gift_cards.gift_cards source on source.id = share.source_gift_card_id
            join lateral (
                select coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as amount
                from ledger.entries entry
                where entry.account_id = source.ledger_account_id
            ) balance on true
            where share.funding_organization_id = @organization_id
              and share.state in ('Pending', 'Claiming')
              and (
                  source.id is null
                  or source.lifecycle_state <> 'Active'
                  or source.ownership_state <> 'IdentityOwned'
                  or source.owner_user_id is distinct from share.sender_user_id
              )

            union all

            select
                'sharing.reservation.exceeds_balance',
                'Error',
                'GiftCard',
                source.id::text,
                source.currency,
                balance.amount,
                reservation.amount,
                'Active share reservations exceed the source card Ledger balance.'
            from gift_cards.gift_cards source
            join lateral (
                select coalesce(sum(share.amount), 0) as amount
                from sharing.shares share
                where share.source_gift_card_id = source.id
                  and share.state in ('Pending', 'Claiming')
            ) reservation on true
            join lateral (
                select coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as amount
                from ledger.entries entry
                where entry.account_id = source.ledger_account_id
            ) balance on true
            where source.funding_organization_id = @organization_id
              and reservation.amount > balance.amount

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'sharing.claimed_without_transfer'
                    when ledger_transaction.operation_type <> 'gift_card.share_transfer'
                        then 'sharing.transfer.operation_mismatch'
                    when coalesce(source_effect.amount, 0) <> -share.amount
                        then 'sharing.transfer.source_amount_mismatch'
                    when coalesce(child_effect.amount, 0) <> share.amount
                        then 'sharing.transfer.child_amount_mismatch'
                    else 'sharing.child_lineage_mismatch'
                end,
                'Error',
                'GiftCardShare',
                share.id::text,
                share.currency,
                share.amount,
                case
                    when coalesce(source_effect.amount, 0) <> -share.amount
                        then abs(coalesce(source_effect.amount, 0))
                    else coalesce(child_effect.amount, 0)
                end,
                'A claimed share does not match its immutable Ledger transfer and child-card lineage.'
            from sharing.shares share
            left join gift_cards.gift_cards source on source.id = share.source_gift_card_id
            left join gift_cards.gift_cards child on child.id = share.child_gift_card_id
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = share.ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                where entry.transaction_id = share.ledger_transaction_id
                  and entry.account_id = source.ledger_account_id
                  and entry.currency = share.currency
            ) source_effect on true
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                where entry.transaction_id = share.ledger_transaction_id
                  and entry.account_id = child.ledger_account_id
                  and entry.currency = share.currency
            ) child_effect on true
            where share.funding_organization_id = @organization_id
              and share.state = 'Claimed'
              and (
                  ledger_transaction.id is null
                  or source.id is null
                  or ledger_transaction.operation_type <> 'gift_card.share_transfer'
                  or coalesce(source_effect.amount, 0) <> -share.amount
                  or coalesce(child_effect.amount, 0) <> share.amount
                  or child.id is null
                  or child.source_gift_card_id is distinct from source.id
                  or child.root_gift_card_id is distinct from source.root_gift_card_id
                  or child.generation <> source.generation + 1
                  or child.owner_user_id is distinct from share.claimed_by_user_id
                  or child.ownership_state <> 'IdentityOwned'
                  or child.initial_value <> share.amount
                  or child.currency <> share.currency
                  or child.funding_organization_id <> share.funding_organization_id
              )

            union all

            select
                'sharing.child_without_claim',
                'Error',
                'GiftCard',
                child.id::text,
                child.currency,
                child.initial_value,
                null::numeric,
                'A shared child card has no matching claimed Sharing record.'
            from gift_cards.gift_cards child
            left join sharing.shares share
              on share.child_gift_card_id = child.id
              and share.ledger_transaction_id = child.issuance_ledger_transaction_id
              and share.state = 'Claimed'
            where child.funding_organization_id = @organization_id
              and child.source_gift_card_id is not null
              and share.id is null

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'payment.confirmation.transaction_missing'
                    when ledger_transaction.operation_type <> 'gift_card.redemption'
                        then 'payment.confirmation.operation_mismatch'
                    else 'payment.confirmation.amount_mismatch'
                end,
                'Error',
                'PaymentProvision',
                provision.id::text,
                provision.currency,
                -provision.confirmed_amount,
                coalesce(card_effect.amount, 0),
                'A confirmed payment provision does not match its gift-card Ledger redemption.'
            from payments.payment_provisions provision
            left join gift_cards.gift_cards card on card.id = provision.gift_card_id
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = provision.redemption_ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                where entry.transaction_id = provision.redemption_ledger_transaction_id
                  and entry.account_id = card.ledger_account_id
                  and entry.currency = provision.currency
            ) card_effect on true
            where provision.funding_organization_id = @organization_id
              and provision.state = 'Confirmed'
              and (
                  ledger_transaction.id is null
                  or card.id is null
                  or ledger_transaction.operation_type <> 'gift_card.redemption'
                  or coalesce(card_effect.amount, 0) <> -provision.confirmed_amount
              )

            union all

            select
                case
                    when ledger_transaction.id is null
                        then 'payment.refund.transaction_missing'
                    when ledger_transaction.operation_type <> 'gift_card.refund'
                        then 'payment.refund.operation_mismatch'
                    else 'payment.refund.amount_mismatch'
                end,
                'Error',
                'PaymentRefund',
                refund.id::text,
                refund.currency,
                refund.amount,
                coalesce(card_effect.amount, 0),
                'A payment refund does not match its gift-card Ledger credit.'
            from payments.payment_refunds refund
            left join gift_cards.gift_cards card on card.id = refund.gift_card_id
            left join relevant_transactions ledger_transaction
              on ledger_transaction.id = refund.refund_ledger_transaction_id
            left join lateral (
                select sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ) as amount
                from ledger.entries entry
                where entry.transaction_id = refund.refund_ledger_transaction_id
                  and entry.account_id = card.ledger_account_id
                  and entry.currency = refund.currency
            ) card_effect on true
            where refund.funding_organization_id = @organization_id
              and (
                  ledger_transaction.id is null
                  or card.id is null
                  or ledger_transaction.operation_type <> 'gift_card.refund'
                  or coalesce(card_effect.amount, 0) <> refund.amount
              )

            union all

            select
                'payment.reservation.exceeds_balance',
                'Error',
                'GiftCard',
                card.id::text,
                card.currency,
                balance.amount,
                reservation.amount,
                'Combined active share and payment reservations exceed the gift-card Ledger balance.'
            from gift_cards.gift_cards card
            join lateral (
                select
                    coalesce((
                        select sum(share.amount)
                        from sharing.shares share
                        where share.source_gift_card_id = card.id
                          and share.state in ('Pending', 'Claiming')
                    ), 0)
                    + coalesce((
                        select sum(provision.amount)
                        from payments.payment_provisions provision
                        where provision.gift_card_id = card.id
                          and provision.state = 'Active'
                          and provision.expires_at_utc > now()
                    ), 0) as amount
            ) reservation on true
            join lateral (
                select coalesce(sum(
                    case entry.direction
                        when 'Credit' then entry.amount
                        else -entry.amount
                    end
                ), 0) as amount
                from ledger.entries entry
                where entry.account_id = card.ledger_account_id
            ) balance on true
            where card.funding_organization_id = @organization_id
              and reservation.amount > balance.amount

            union all

            select
                'ledger.redemption_settlement.mismatch',
                'Error',
                'PlatformRedemptionSettlement',
                coalesce(expected.currency, actual.currency),
                coalesce(expected.currency, actual.currency),
                coalesce(expected.amount, 0),
                coalesce(actual.amount, 0),
                'The platform redemption-settlement balance does not match confirmed redemptions.'
            from settlement_expected expected
            full join settlement_actual actual using (currency)
            where coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
              and coalesce(expected.amount, 0) <> coalesce(actual.amount, 0)

            union all

            select
                'ledger.transaction.orphan',
                'Error',
                'LedgerTransaction',
                ledger_transaction.id::text,
                null::text,
                null::numeric,
                null::numeric,
                'A Phase 2 Ledger transaction has no matching authoritative domain record.'
            from relevant_transactions ledger_transaction
            left join corporate_credits.allocations allocation
              on allocation.ledger_transaction_id = ledger_transaction.id
            left join corporate_credits.reversals reversal
              on reversal.ledger_transaction_id = ledger_transaction.id
            left join gift_cards.gift_cards card
              on card.issuance_ledger_transaction_id = ledger_transaction.id
            left join gift_cards.lifecycle_events lifecycle
              on lifecycle.ledger_transaction_id = ledger_transaction.id
            left join sharing.shares share
              on share.ledger_transaction_id = ledger_transaction.id
            left join payments.payment_provisions provision
              on provision.redemption_ledger_transaction_id = ledger_transaction.id
            left join payments.payment_refunds refund
              on refund.refund_ledger_transaction_id = ledger_transaction.id
            where allocation.id is null
              and reversal.id is null
              and card.id is null
              and lifecycle.id is null
              and share.id is null
              and provision.id is null
              and refund.id is null
        )
        select
            code,
            severity,
            entity_type,
            entity_id,
            currency,
            expected_amount,
            actual_amount,
            message
        from findings
        order by code, entity_type, entity_id, currency
        limit @take
        """;
}
