using System.Net;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class CorporateCreditAllocationTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Successful_allocation_posts_balanced_ledger_and_audit_atomically()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = AllocationClient();
        var request = NewRequest(organizationId);

        var response = await client.PostAsJsonAsync(Route, request);

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AllocationResponse>())!;
        Assert.Equal(organizationId, result.OrganizationId);
        Assert.Equal(request.Amount, result.Amount);
        Assert.Equal("TRY", result.Currency);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.allocations where id = @id",
                command => command.Parameters.AddWithValue("id", result.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from ledger.transactions where id = @id",
                command => command.Parameters.AddWithValue("id", result.LedgerTransactionId)));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                "select count(*) from ledger.entries where transaction_id = @id",
                command => command.Parameters.AddWithValue("id", result.LedgerTransactionId)));
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                """
                select count(*)
                from (
                    select currency
                    from ledger.entries
                    where transaction_id = @id
                    group by currency
                    having sum(case direction when 'Credit' then amount else -amount end) <> 0
                ) imbalance
                """,
                command => command.Parameters.AddWithValue("id", result.LedgerTransactionId)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where operation = 'corporate_credit.allocated'
                  and entity_id = @entity_id
                """,
                command => command.Parameters.AddWithValue("entity_id", result.Id.ToString())));
    }

    [Fact]
    public async Task Identical_retry_returns_the_original_without_new_value()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = AllocationClient();
        var request = NewRequest(organizationId);

        var first = await client.PostAsJsonAsync(Route, request);
        var second = await client.PostAsJsonAsync(Route, request);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var firstResult = (await first.Content.ReadFromJsonAsync<AllocationResponse>())!;
        var secondResult = (await second.Content.ReadFromJsonAsync<AllocationResponse>())!;
        Assert.Equal(firstResult, secondResult);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.allocations where idempotency_key = @key",
                command => command.Parameters.AddWithValue("key", request.IdempotencyKey)));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                "select count(*) from ledger.entries where transaction_id = @id",
                command => command.Parameters.AddWithValue("id", firstResult.LedgerTransactionId)));
    }

    [Fact]
    public async Task Reusing_a_key_for_different_intent_is_a_conflict()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = AllocationClient();
        var request = NewRequest(organizationId);
        (await client.PostAsJsonAsync(Route, request)).EnsureSuccessStatusCode();

        var conflict = await client.PostAsJsonAsync(Route, request with { Amount = request.Amount + 1m });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Allocation_requires_the_named_platform_permission()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var request = NewRequest(organizationId);

        var unauthenticated = await fixture.Factory.CreateClient().PostAsJsonAsync(Route, request);
        var wrongPermission = await MembershipTestSupport
            .PlatformOperator(fixture, PlatformPermissions.OrganizationsView)
            .PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPermission.StatusCode);
    }

    [Fact]
    public async Task Subsidiary_is_not_an_eligible_corporate_credit_recipient()
    {
        var rootId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var organizationClient = MembershipTestSupport.OrganizationMember(
            fixture,
            rootId,
            OrganizationPermissions.CreateSubsidiary);
        var subsidiaryResponse = await organizationClient.PostAsJsonAsync(
            $"/api/v1/organizations/{rootId}/subsidiaries",
            new
            {
                name = "Financially Ineligible Subsidiary",
                code = "FIN" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            });
        subsidiaryResponse.EnsureSuccessStatusCode();
        var subsidiary = (await subsidiaryResponse.Content.ReadFromJsonAsync<OrganizationResponse>())!;

        var response = await AllocationClient().PostAsJsonAsync(Route, NewRequest(subsidiary.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.allocations where organization_id = @id",
                command => command.Parameters.AddWithValue("id", subsidiary.Id)));
    }

    [Fact]
    public async Task Runtime_role_cannot_mutate_committed_financial_history()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var response = await AllocationClient().PostAsJsonAsync(Route, NewRequest(organizationId));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AllocationResponse>())!;

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var update = session.Command(
            "update ledger.entries set amount = amount + 1 where transaction_id = @id");
        update.Parameters.AddWithValue("id", result.LedgerTransactionId);
        var updateError = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", updateError.SqlState);

        await using var deleteSession = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var delete = deleteSession.Command(
            "delete from corporate_credits.allocations where id = @id");
        delete.Parameters.AddWithValue("id", result.Id);
        var deleteError = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("42501", deleteError.SqlState);
    }

    [Fact]
    public async Task Financial_rows_are_isolated_between_customer_tenants()
    {
        var firstOrganization = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var secondOrganization = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var response = await AllocationClient().PostAsJsonAsync(Route, NewRequest(firstOrganization));
        response.EnsureSuccessStatusCode();

        await using var first = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, firstOrganization);
        await using var second = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, secondOrganization);

        Assert.Equal(1, await first.ScalarCountAsync("select count(*) from corporate_credits.allocations"));
        Assert.Equal(1, await first.ScalarCountAsync("select count(*) from ledger.transactions"));
        Assert.Equal(0, await second.ScalarCountAsync("select count(*) from corporate_credits.allocations"));
        Assert.Equal(0, await second.ScalarCountAsync("select count(*) from ledger.transactions"));
    }

    [Fact]
    public async Task Concurrent_identical_requests_cannot_duplicate_value()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var client = AllocationClient();
        var request = NewRequest(organizationId);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(Route, request),
            client.PostAsJsonAsync(Route, request));

        Assert.Contains(responses, response => response.IsSuccessStatusCode);
        Assert.All(
            responses,
            response => Assert.Contains(
                response.StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from corporate_credits.allocations where idempotency_key = @key",
                command => command.Parameters.AddWithValue("key", request.IdempotencyKey)));
    }

    private const string Route = "/api/v1/corporate-credits/allocations";

    private HttpClient AllocationClient() =>
        MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.CorporateCreditsAllocate);

    private static AllocationRequest NewRequest(Guid organizationId) =>
        new(
            organizationId,
            1250.50m,
            "TRY",
            "CONTRACT-" + Guid.NewGuid().ToString("N"),
            "allocation-" + Guid.NewGuid().ToString("N"));

    private sealed record AllocationRequest(
        Guid OrganizationId,
        decimal Amount,
        string Currency,
        string BusinessReference,
        string IdempotencyKey);

    private sealed record AllocationResponse(
        Guid Id,
        Guid OrganizationId,
        Guid LedgerTransactionId,
        decimal Amount,
        string Currency,
        string BusinessReference,
        string IdempotencyKey,
        DateTimeOffset AllocatedAtUtc);

    private sealed record OrganizationResponse(Guid Id);
}
