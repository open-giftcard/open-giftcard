using System.Net;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class CorporateCreditQueryTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Organization_member_reads_ledger_derived_multi_currency_balances_and_history()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var platform = FinancialPlatformClient();

        await AllocateAsync(platform, organizationId, 1250.5000m, "TRY");
        await AllocateAsync(platform, organizationId, 249.5000m, "TRY");
        await AllocateAsync(platform, organizationId, 42.1250m, "USD");

        var organization = MembershipTestSupport.OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.CorporateCreditsView);

        var balancesResponse = await organization.GetAsync(BalanceRoute(organizationId));
        balancesResponse.EnsureSuccessStatusCode();
        var balances = (await balancesResponse.Content
            .ReadFromJsonAsync<IReadOnlyList<BalanceResponse>>())!;

        Assert.Collection(
            balances,
            tryBalance =>
            {
                Assert.Equal("TRY", tryBalance.Currency);
                Assert.Equal(1500.0000m, tryBalance.Amount);
            },
            usd =>
            {
                Assert.Equal("USD", usd.Currency);
                Assert.Equal(42.1250m, usd.Amount);
            });

        var historyResponse = await organization.GetAsync(HistoryRoute(organizationId));
        historyResponse.EnsureSuccessStatusCode();
        var history = (await historyResponse.Content.ReadFromJsonAsync<HistoryPageResponse>())!;

        Assert.Equal(3, history.Items.Count);
        Assert.All(history.Items, item => Assert.Equal(organizationId, item.OrganizationId));
        Assert.Null(history.NextCursor);
        Assert.True(history.Items.SequenceEqual(
            history.Items
                .OrderByDescending(item => item.AllocatedAtUtc)
                .ThenByDescending(item => item.Id)));
    }

    [Fact]
    public async Task Allocation_history_cursor_is_stable_without_repeating_rows()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var platform = FinancialPlatformClient();
        for (var index = 0; index < 5; index++)
        {
            await AllocateAsync(platform, organizationId, 100m + index, "TRY");
        }

        var first = (await platform.GetFromJsonAsync<HistoryPageResponse>(
            HistoryRoute(organizationId) + "?limit=2"))!;

        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = (await platform.GetFromJsonAsync<HistoryPageResponse>(
            HistoryRoute(organizationId) +
            "?limit=2&cursor=" +
            Uri.EscapeDataString(first.NextCursor!)))!;

        Assert.Equal(2, second.Items.Count);
        Assert.NotNull(second.NextCursor);

        var third = (await platform.GetFromJsonAsync<HistoryPageResponse>(
            HistoryRoute(organizationId) +
            "?limit=2&cursor=" +
            Uri.EscapeDataString(second.NextCursor!)))!;

        Assert.Single(third.Items);
        Assert.Null(third.NextCursor);

        var ids = first.Items
            .Concat(second.Items)
            .Concat(third.Items)
            .Select(item => item.Id)
            .ToList();
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task Financial_reads_require_the_named_permission_for_each_caller_kind()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        await AllocateAsync(FinancialPlatformClient(), organizationId, 25m, "TRY");

        var anonymous = fixture.Factory.CreateClient();
        var wrongPlatformPermission = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.OrganizationsView);
        var wrongOrganizationPermission = MembershipTestSupport.OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.View);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(BalanceRoute(organizationId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await wrongPlatformPermission.GetAsync(BalanceRoute(organizationId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await wrongOrganizationPermission.GetAsync(HistoryRoute(organizationId))).StatusCode);
    }

    [Fact]
    public async Task Organization_scope_and_rls_prevent_cross_tenant_financial_reads()
    {
        var firstOrganization = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var secondOrganization = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        await AllocateAsync(FinancialPlatformClient(), secondOrganization, 75m, "TRY");

        var firstOrganizationClient = MembershipTestSupport.OrganizationMember(
            fixture,
            firstOrganization,
            OrganizationPermissions.CorporateCreditsView);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await firstOrganizationClient.GetAsync(BalanceRoute(secondOrganization))).StatusCode);

        await using var firstScope = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            firstOrganization);
        Assert.Equal(
            0,
            await firstScope.ScalarCountAsync(
                "select count(*) from corporate_credits.allocations where organization_id = @id",
                command => command.Parameters.AddWithValue("id", secondOrganization)));
        Assert.Equal(
            0,
            await firstScope.ScalarCountAsync(
                """
                select count(*)
                from ledger.accounts
                where organization_id = @id
                  and type = 'OrganizationCorporateCredit'
                """,
                command => command.Parameters.AddWithValue("id", secondOrganization)));
    }

    [Fact]
    public async Task Empty_financial_state_and_invalid_history_input_are_explicit()
    {
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        var platform = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.CorporateCreditsView);

        var balances = (await platform.GetFromJsonAsync<IReadOnlyList<BalanceResponse>>(
            BalanceRoute(organizationId)))!;
        var history = (await platform.GetFromJsonAsync<HistoryPageResponse>(
            HistoryRoute(organizationId)))!;
        var invalidCursor = await platform.GetAsync(
            HistoryRoute(organizationId) + "?cursor=not-a-valid-cursor");
        var invalidLimit = await platform.GetAsync(
            HistoryRoute(organizationId) + "?limit=201");

        Assert.Empty(balances);
        Assert.Empty(history.Items);
        Assert.Null(history.NextCursor);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
    }

    private HttpClient FinancialPlatformClient() =>
        MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.CorporateCreditsAllocate,
            PlatformPermissions.CorporateCreditsView);

    private static async Task AllocateAsync(
        HttpClient client,
        Guid organizationId,
        decimal amount,
        string currency)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/corporate-credits/allocations",
            new
            {
                organizationId,
                amount,
                currency,
                businessReference = "QUERY-" + nonce,
                idempotencyKey = "query-allocation-" + nonce,
            });
        response.EnsureSuccessStatusCode();
    }

    private static string BalanceRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/corporate-credits/balances";

    private static string HistoryRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/corporate-credits/allocations";

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
        DateTimeOffset AllocatedAtUtc);
}
