using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace GiftCardPlatform.Modules.Reporting.Application;

internal sealed class PaymentReportingQuery(
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : IPaymentReportingQuery
{
    public async Task<PaymentReportPage> GetPaymentsAsync(
        PaymentReportRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePage(request);
        RequirePaymentViewer();
        var filters = PaymentReportingSearchFilters.Normalize(request);
        var cursor = ReportingCursorCodec.DecodeFiltered(
            request.Cursor,
            "reporting.payments",
            filters.Fingerprint);
        var cursorId = ParseCursorId(cursor);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = await GetPageAsync(
            request.Limit,
            filters,
            cursor,
            cursorId,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var hasMore = items.Count > request.Limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var (totalMatchingPayments, matchingTotals) = await GetMatchingTotalsAsync(
            filters,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var nextCursor = hasMore && items.Count > 0
            ? ReportingCursorCodec.EncodeFiltered(
                items[^1].CreatedAtUtc,
                items[^1].PaymentProvisionId.ToString("N"),
                filters.Fingerprint)
            : null;
        return new PaymentReportPage(
            items,
            request.Limit,
            nextCursor,
            totalMatchingPayments,
            CalculateTotals(items),
            matchingTotals);
    }

    public async Task<PaymentReceiptReport> GetPaymentAsync(
        Guid paymentProvisionId,
        CancellationToken cancellationToken)
    {
        if (paymentProvisionId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "reporting.payments.payment_provision_id.invalid",
                "A payment provision identifier is required.");
        }

        RequirePaymentViewer();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        PaymentReportItem payment;
        await using (var command = CreateCommand(PaymentDetailSql, transaction))
        {
            command.Parameters.AddWithValue("payment_provision_id", paymentProvisionId);
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new NotFoundException(
                    "reporting.payment.not_found",
                    "Payment not found.");
            }

            payment = ReadPayment(reader);
        }

        var refunds = new List<PaymentRefundReportLine>();
        await using (var command = CreateCommand(RefundDetailSql, transaction))
        {
            command.Parameters.AddWithValue("payment_provision_id", paymentProvisionId);
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                refunds.Add(new PaymentRefundReportLine(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetDecimal(6),
                    reader.GetGuid(7),
                    reader.GetFieldValue<DateTimeOffset>(8)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PaymentReceiptReport(payment, refunds);
    }

    private static async Task<List<PaymentReportItem>> GetPageAsync(
        int limit,
        PaymentReportingSearchFilters filters,
        ReportingCursor? cursor,
        Guid? cursorId,
        IModuleTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(PaymentPageSql, transaction);
        AddFilterParameters(command, filters);
        AddNullableTimestamp(command, "cursor_at", cursor?.OccurredAtUtc);
        AddNullableUuid(command, "cursor_id", cursorId);
        command.Parameters.AddWithValue("take", limit + 1);
        var items = new List<PaymentReportItem>(limit + 1);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadPayment(reader));
        }

        return items;
    }

    private static async Task<(long Count, IReadOnlyList<PaymentReportCurrencyTotals> Totals)>
        GetMatchingTotalsAsync(
            PaymentReportingSearchFilters filters,
            IModuleTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(PaymentTotalsSql, transaction);
        AddFilterParameters(command, filters);
        var totals = new List<PaymentReportCurrencyTotals>();
        long totalCount = 0;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var currencyTotals = ReadTotals(reader);
            totalCount = checked(totalCount + currencyTotals.PaymentCount);
            totals.Add(currencyTotals);
        }

        return (totalCount, totals);
    }

    private void RequirePaymentViewer()
    {
        if (!executionContext.IsPlatformOperator ||
            executionContext.IsSystem ||
            !executionContext.HasPlatformPermission(PlatformPermissions.PaymentsView))
        {
            throw new ForbiddenException(
                "reporting.payments.permission.required",
                "Platform payment-view permission is required.");
        }
    }

    private static void ValidatePage(PaymentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > ReportingPageRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "reporting.payments.limit.invalid",
                $"Limit must be between 1 and {ReportingPageRequest.MaxLimit}.");
        }
    }

    private static Guid? ParseCursorId(ReportingCursor? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(cursor.StableKey, "N", out var id) || id == Guid.Empty)
        {
            throw new ValidationFailedException(
                "reporting.payments.cursor.invalid",
                "The reporting cursor is invalid.");
        }

        return id;
    }

    private static PaymentReportItem ReadPayment(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetGuid(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetGuid(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetDecimal(11),
            reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            reader.GetDecimal(13),
            reader.GetDecimal(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetBoolean(17),
            reader.GetInt32(18),
            reader.GetFieldValue<DateTimeOffset>(19),
            reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
            reader.IsDBNull(21) ? null : reader.GetGuid(21));

    private static PaymentReportCurrencyTotals ReadTotals(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8));

    private static PaymentReportCurrencyTotals[] CalculateTotals(
        IReadOnlyList<PaymentReportItem> items) =>
        items
            .GroupBy(item => item.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PaymentReportCurrencyTotals(
                group.Key,
                group.LongCount(),
                group.LongCount(item => item.ConfirmedAmount is not null),
                group.Sum(item => (long)item.RefundCount),
                group.LongCount(item => item.IsFullyReversed),
                group.Sum(item => item.ProvisionedAmount),
                group.Sum(item => item.ConfirmedAmount ?? 0m),
                group.Sum(item => item.RefundedAmount),
                group.Sum(item => item.NetAmount)))
            .ToArray();

    private static void AddFilterParameters(
        NpgsqlCommand command,
        PaymentReportingSearchFilters filters)
    {
        AddNullableUuid(command, "pos_client_id", filters.PosClientId);
        AddNullableUuid(command, "pos_terminal_id", filters.PosTerminalId);
        AddNullableUuid(command, "funding_organization_id", filters.FundingOrganizationId);
        AddNullableText(command, "store_reference", filters.StoreReference);
        AddNullableText(command, "state", filters.State);
        AddNullableText(command, "currency", filters.Currency);
        AddNullableText(command, "reference_pattern", filters.ReferencePattern);
        AddNullableTimestamp(command, "occurred_from_utc", filters.OccurredFromUtc);
        AddNullableTimestamp(command, "occurred_before_utc", filters.OccurredBeforeUtc);
    }

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid?>(name, NpgsqlDbType.Uuid)
        {
            TypedValue = value,
        });

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter<string?>(name, NpgsqlDbType.Text)
        {
            TypedValue = value,
        });

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>(name, NpgsqlDbType.TimestampTz)
        {
            TypedValue = value,
        });

    private static NpgsqlCommand CreateCommand(string sql, IModuleTransaction transaction) =>
        new(sql, transaction.Transaction.Connection, transaction.Transaction);

    private const string PaymentRowsSql = """
        select
            provision.id,
            provision.funding_organization_id,
            provision.gift_card_id,
            provision.gift_card_public_reference,
            provision.pos_client_id,
            client.code,
            client.display_name,
            provision.pos_terminal_id,
            terminal.code,
            provision.store_reference,
            provision.pos_transaction_reference,
            provision.amount,
            provision.confirmed_amount,
            coalesce(refunds.refunded_amount, 0)::numeric(20,4) as refunded_amount,
            (coalesce(provision.confirmed_amount, 0) -
                coalesce(refunds.refunded_amount, 0))::numeric(20,4) as net_amount,
            provision.currency,
            provision.state,
            (provision.confirmed_amount is not null and
                coalesce(refunds.refunded_amount, 0) = provision.confirmed_amount) as is_fully_reversed,
            coalesce(refunds.refund_count, 0)::integer as refund_count,
            provision.created_at_utc,
            provision.settled_at_utc,
            provision.redemption_ledger_transaction_id
        from payments.payment_provisions provision
        join payments.pos_clients client on client.id = provision.pos_client_id
        join payments.pos_terminals terminal on terminal.id = provision.pos_terminal_id
        left join lateral (
            select
                coalesce(sum(refund.amount), 0)::numeric(20,4) as refunded_amount,
                count(*)::integer as refund_count
            from payments.payment_refunds refund
            where refund.payment_provision_id = provision.id
        ) refunds on true
        """;

    private const string FilterSql = """
        where (@pos_client_id is null or provision.pos_client_id = @pos_client_id)
          and (@pos_terminal_id is null or provision.pos_terminal_id = @pos_terminal_id)
          and (@funding_organization_id is null or provision.funding_organization_id = @funding_organization_id)
          and (@store_reference is null or provision.store_reference = @store_reference)
          and (@state is null or provision.state = @state)
          and (@currency is null or provision.currency = @currency)
          and (@reference_pattern is null or
               lower(coalesce(provision.pos_transaction_reference, '')) like @reference_pattern escape '\' or
               lower(provision.gift_card_public_reference) like @reference_pattern escape '\' or
               lower(client.code) like @reference_pattern escape '\' or
               lower(terminal.code) like @reference_pattern escape '\')
          and (@occurred_from_utc is null or provision.created_at_utc >= @occurred_from_utc)
          and (@occurred_before_utc is null or provision.created_at_utc < @occurred_before_utc)
        """;

    private const string PaymentPageSql = PaymentRowsSql + "\n" + FilterSql + "\n" + """
          and (@cursor_at is null or
               provision.created_at_utc < @cursor_at or
               (provision.created_at_utc = @cursor_at and provision.id < @cursor_id))
        order by provision.created_at_utc desc, provision.id desc
        limit @take
        """;

    private const string PaymentDetailSql = PaymentRowsSql + "\n" + """
        where provision.id = @payment_provision_id
        """;

    private const string PaymentTotalsSql = """
        with matching as (
        """ + PaymentRowsSql + "\n" + FilterSql + "\n" + """
        )
        select
            currency,
            count(*)::bigint,
            count(*) filter (where confirmed_amount is not null)::bigint,
            coalesce(sum(refund_count), 0)::bigint,
            count(*) filter (where is_fully_reversed)::bigint,
            coalesce(sum(amount), 0)::numeric(20,4),
            coalesce(sum(confirmed_amount), 0)::numeric(20,4),
            coalesce(sum(refunded_amount), 0)::numeric(20,4),
            coalesce(sum(net_amount), 0)::numeric(20,4)
        from matching
        group by currency
        order by currency
        """;

    private const string RefundDetailSql = """
        select
            refund.id,
            refund.pos_terminal_id,
            terminal.code,
            refund.store_reference,
            refund.pos_transaction_reference,
            refund.reason,
            refund.amount,
            refund.refund_ledger_transaction_id,
            refund.refunded_at_utc
        from payments.payment_refunds refund
        join payments.pos_terminals terminal on terminal.id = refund.pos_terminal_id
        where refund.payment_provision_id = @payment_provision_id
        order by refund.refunded_at_utc, refund.id
        """;
}
