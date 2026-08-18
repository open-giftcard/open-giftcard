using System.Net;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class CorporateCreditReversalTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Successful_reversal_posts_compensating_ledger_and_audit_without_mutating_original()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = FinancialClient();
        var allocation = await AllocateAsync(client, organizationId, 300m);

        var response = await client.PostAsJsonAsync(
            ReversalRoute(allocation.Id),
            NewReversalRequest());

        response.EnsureSuccessStatusCode();
        var reversal = (await response.Content.ReadFromJsonAsync<ReversalResponse>())!;
        Assert.Equal(allocation.Id, reversal.AllocationId);
        Assert.Equal(organizationId, reversal.OrganizationId);
        Assert.Equal(300m, reversal.Amount);
        Assert.Equal("TRY", reversal.Currency);

        var balances = (await client.GetFromJsonAsync<IReadOnlyList<BalanceResponse>>(
            BalanceRoute(organizationId)))!;
        Assert.Single(balances);
        Assert.Equal(0m, balances[0].Amount);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.allocations where id = @id",
                command => command.Parameters.AddWithValue("id", allocation.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.reversals where id = @id",
                command => command.Parameters.AddWithValue("id", reversal.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where id = @id
                  and operation_type = 'corporate_credit.reversal'
                  and reverses_transaction_id = @original_id
                """,
                command =>
                {
                    command.Parameters.AddWithValue("id", reversal.LedgerTransactionId);
                    command.Parameters.AddWithValue("original_id", allocation.LedgerTransactionId);
                }));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                "select count(*) from ledger.entries where transaction_id = @id",
                command => command.Parameters.AddWithValue("id", reversal.LedgerTransactionId)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.entries entry
                join ledger.accounts account on account.id = entry.account_id
                where entry.transaction_id = @id
                  and entry.direction = 'Debit'
                  and account.type = 'OrganizationCorporateCredit'
                  and entry.amount = 300
                """,
                command => command.Parameters.AddWithValue("id", reversal.LedgerTransactionId)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where operation = 'corporate_credit.reversed'
                  and entity_id = @id
                """,
                command => command.Parameters.AddWithValue("id", reversal.Id.ToString())));
    }

    [Fact]
    public async Task Identical_retry_returns_original_and_changed_intent_conflicts()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = FinancialClient();
        var allocation = await AllocateAsync(client, organizationId, 200m);
        var request = NewReversalRequest();

        var first = await client.PostAsJsonAsync(ReversalRoute(allocation.Id), request);
        var retry = await client.PostAsJsonAsync(ReversalRoute(allocation.Id), request);
        var changedReason = await client.PostAsJsonAsync(
            ReversalRoute(allocation.Id),
            request with { Reason = "A different correction reason" });
        var changedKey = await client.PostAsJsonAsync(
            ReversalRoute(allocation.Id),
            request with { IdempotencyKey = "reversal-" + Guid.NewGuid().ToString("N") });

        first.EnsureSuccessStatusCode();
        retry.EnsureSuccessStatusCode();
        Assert.Equal(
            await first.Content.ReadFromJsonAsync<ReversalResponse>(),
            await retry.Content.ReadFromJsonAsync<ReversalResponse>());
        Assert.Equal(HttpStatusCode.Conflict, changedReason.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedKey.StatusCode);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.reversals where allocation_id = @id",
                command => command.Parameters.AddWithValue("id", allocation.Id)));
    }

    [Fact]
    public async Task Reversal_requires_named_permission_and_known_allocation()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var allocation = await AllocateAsync(FinancialClient(), organizationId, 100m);
        var request = NewReversalRequest();

        var anonymous = await fixture.Factory.CreateClient()
            .PostAsJsonAsync(ReversalRoute(allocation.Id), request);
        var wrongPermission = await MembershipTestSupport
            .PlatformOperator(fixture, PlatformPermissions.CorporateCreditsAllocate)
            .PostAsJsonAsync(ReversalRoute(allocation.Id), request);
        var unknown = await FinancialClient()
            .PostAsJsonAsync(ReversalRoute(Guid.CreateVersion7()), NewReversalRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPermission.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Reversal_refuses_when_allocated_value_has_already_been_consumed()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = FinancialClient();
        var allocation = await AllocateAsync(client, organizationId, 300m);
        await DrainCorporateCreditAsync(organizationId, 250m);

        var response = await client.PostAsJsonAsync(
            ReversalRoute(allocation.Id),
            NewReversalRequest());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.reversals where allocation_id = @id",
                command => command.Parameters.AddWithValue("id", allocation.Id)));
    }

    [Fact]
    public async Task Concurrent_reversal_attempts_cannot_reverse_one_allocation_twice()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = FinancialClient();
        var allocation = await AllocateAsync(client, organizationId, 400m);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                ReversalRoute(allocation.Id),
                NewReversalRequest("reversal-" + Guid.NewGuid().ToString("N"))),
            client.PostAsJsonAsync(
                ReversalRoute(allocation.Id),
                NewReversalRequest("reversal-" + Guid.NewGuid().ToString("N"))));

        Assert.Contains(responses, response => response.IsSuccessStatusCode);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.reversals where allocation_id = @id",
                command => command.Parameters.AddWithValue("id", allocation.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where reverses_transaction_id = @id
                """,
                command => command.Parameters.AddWithValue("id", allocation.LedgerTransactionId)));
    }

    [Fact]
    public async Task Reversal_is_visible_in_history_but_isolated_from_other_tenants()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var otherOrganizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = FinancialClient();
        var allocation = await AllocateAsync(client, organizationId, 150m);
        var reversalResponse = await client.PostAsJsonAsync(
            ReversalRoute(allocation.Id),
            NewReversalRequest());
        reversalResponse.EnsureSuccessStatusCode();
        var reversal = (await reversalResponse.Content.ReadFromJsonAsync<ReversalResponse>())!;

        var organizationClient = MembershipTestSupport.OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.CorporateCreditsView);
        var history = (await organizationClient.GetFromJsonAsync<HistoryPageResponse>(
            HistoryRoute(organizationId)))!;

        var item = Assert.Single(history.Items);
        Assert.NotNull(item.Reversal);
        Assert.Equal(reversal.Id, item.Reversal.Id);

        await using var otherScope = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            otherOrganizationId);
        Assert.Equal(
            0,
            await otherScope.ScalarCountAsync("select count(*) from corporate_credits.reversals"));
    }

    [Fact]
    public async Task Runtime_role_cannot_update_or_delete_reversal_history()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = FinancialClient();
        var allocation = await AllocateAsync(client, organizationId, 90m);
        var response = await client.PostAsJsonAsync(
            ReversalRoute(allocation.Id),
            NewReversalRequest());
        response.EnsureSuccessStatusCode();
        var reversal = (await response.Content.ReadFromJsonAsync<ReversalResponse>())!;

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var update = session.Command(
            "update corporate_credits.reversals set reason = 'changed' where id = @id");
        update.Parameters.AddWithValue("id", reversal.Id);
        var updateError = await Assert.ThrowsAsync<PostgresException>(
            () => update.ExecuteNonQueryAsync());

        await using var deleteSession = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var delete = deleteSession.Command(
            "delete from ledger.transactions where id = @id");
        delete.Parameters.AddWithValue("id", reversal.LedgerTransactionId);
        var deleteError = await Assert.ThrowsAsync<PostgresException>(
            () => delete.ExecuteNonQueryAsync());

        Assert.Equal("42501", updateError.SqlState);
        Assert.Equal("42501", deleteError.SqlState);
    }

    private HttpClient FinancialClient() =>
        MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.CorporateCreditsAllocate,
            PlatformPermissions.CorporateCreditsView,
            PlatformPermissions.CorporateCreditsReverse);

    private static async Task<AllocationResponse> AllocateAsync(
        HttpClient client,
        Guid organizationId,
        decimal amount)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/corporate-credits/allocations",
            new
            {
                organizationId,
                amount,
                currency = "TRY",
                businessReference = "REVERSAL-TEST-" + nonce,
                idempotencyKey = "reversal-test-allocation-" + nonce,
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AllocationResponse>())!;
    }

    private async Task DrainCorporateCreditAsync(Guid organizationId, decimal amount)
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);

        Guid organizationAccountId;
        await using (var account = session.Command(
            """
            select id
            from ledger.accounts
            where organization_id = @organization_id
              and type = 'OrganizationCorporateCredit'
              and currency = 'TRY'
            """))
        {
            account.Parameters.AddWithValue("organization_id", organizationId);
            organizationAccountId = (Guid)(await account.ExecuteScalarAsync())!;
        }

        Guid platformAccountId;
        await using (var account = session.Command(
            """
            select id
            from ledger.accounts
            where organization_id is null
              and type = 'PlatformFunding'
              and currency = 'TRY'
            """))
        {
            platformAccountId = (Guid)(await account.ExecuteScalarAsync())!;
        }

        var transactionId = Guid.CreateVersion7();
        await using (var transaction = session.Command(
            """
            insert into ledger.transactions
                (id, organization_id, operation_type, business_reference,
                 idempotency_key, intent_hash, reverses_transaction_id,
                 initiated_by_user_id, posted_at_utc)
            values
                (@id, @organization_id, 'test.corporate_credit.consume',
                 @reference, @key, @hash, null, @user_id, now())
            """))
        {
            transaction.Parameters.AddWithValue("id", transactionId);
            transaction.Parameters.AddWithValue("organization_id", organizationId);
            transaction.Parameters.AddWithValue("reference", "TEST-DRAIN-" + transactionId);
            transaction.Parameters.AddWithValue("key", "test-drain-" + transactionId);
            transaction.Parameters.AddWithValue("hash", new string('A', 64));
            transaction.Parameters.AddWithValue("user_id", Guid.CreateVersion7());
            await transaction.ExecuteNonQueryAsync();
        }

        foreach (var posting in new[]
                 {
                     (AccountId: organizationAccountId, Direction: "Debit"),
                     (AccountId: platformAccountId, Direction: "Credit"),
                 })
        {
            await using var entry = session.Command(
                """
                insert into ledger.entries
                    (id, transaction_id, organization_id, account_id,
                     direction, amount, currency)
                values
                    (@id, @transaction_id, @organization_id, @account_id,
                     @direction, @amount, 'TRY')
                """);
            entry.Parameters.AddWithValue("id", Guid.CreateVersion7());
            entry.Parameters.AddWithValue("transaction_id", transactionId);
            entry.Parameters.AddWithValue("organization_id", organizationId);
            entry.Parameters.AddWithValue("account_id", posting.AccountId);
            entry.Parameters.AddWithValue("direction", posting.Direction);
            entry.Parameters.AddWithValue("amount", amount);
            await entry.ExecuteNonQueryAsync();
        }

        await session.CommitAsync();
    }

    private static ReversalRequest NewReversalRequest(string? idempotencyKey = null) =>
        new(
            "Commercial agreement cancelled",
            idempotencyKey ?? "reversal-" + Guid.NewGuid().ToString("N"));

    private static string ReversalRoute(Guid allocationId) =>
        $"/api/v1/corporate-credits/allocations/{allocationId}/reversal";

    private static string BalanceRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/corporate-credits/balances";

    private static string HistoryRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/corporate-credits/allocations";

    private sealed record ReversalRequest(string Reason, string IdempotencyKey);

    private sealed record AllocationResponse(
        Guid Id,
        Guid OrganizationId,
        Guid LedgerTransactionId,
        decimal Amount,
        string Currency,
        string BusinessReference,
        string IdempotencyKey,
        DateTimeOffset AllocatedAtUtc);

    private sealed record ReversalResponse(
        Guid Id,
        Guid AllocationId,
        Guid OrganizationId,
        Guid LedgerTransactionId,
        decimal Amount,
        string Currency,
        string Reason,
        string IdempotencyKey,
        DateTimeOffset ReversedAtUtc);

    private sealed record BalanceResponse(string Currency, decimal Amount);

    private sealed record HistoryPageResponse(
        IReadOnlyList<HistoryItemResponse> Items,
        int Limit,
        string? NextCursor);

    private sealed record HistoryItemResponse(
        Guid Id,
        Guid OrganizationId,
        Guid LedgerTransactionId,
        decimal Amount,
        string Currency,
        string BusinessReference,
        Guid AllocatedByUserId,
        DateTimeOffset AllocatedAtUtc,
        ReversalSummaryResponse? Reversal);

    private sealed record ReversalSummaryResponse(
        Guid Id,
        Guid LedgerTransactionId,
        string Reason,
        Guid ReversedByUserId,
        DateTimeOffset ReversedAtUtc);
}
