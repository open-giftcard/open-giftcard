using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.AuthorizationTestSupport;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class GiftCardIssuanceTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Issuance_posts_one_balanced_card_account_and_audit_atomically()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 1_000m);
        var request = NewRequest(250m);

        var response = await IssueClient(rootId).PostAsJsonAsync(Route(rootId), request);

        response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<CardResponse>())!;
        Assert.Equal(rootId, card.FundingOrganizationId);
        Assert.Equal(rootId, card.IssuingOrganizationId);
        Assert.Equal(rootId, card.OwnerOrganizationId);
        Assert.Null(card.OwnerUserId);
        Assert.Equal("OrganizationInventory", card.OwnershipState);
        Assert.Equal("Active", card.LifecycleState);
        Assert.Equal(250m, card.FundedAmount);
        Assert.Equal("TRY", card.Currency);
        Assert.False(card.IsTransferable);
        Assert.False(card.IsDivisible);
        Assert.Equal(card.Id, card.RootGiftCardId);
        Assert.Null(card.SourceGiftCardId);
        Assert.Equal(0, card.Generation);
        Assert.Matches("^GC-[0-9A-F]{20}$", card.PublicReference);

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, rootId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.gift_cards
                where id = @id
                  and ledger_account_id = @account_id
                  and issuance_ledger_transaction_id = @transaction_id
                """,
                command =>
                {
                    command.Parameters.AddWithValue("id", card.Id);
                    command.Parameters.AddWithValue("account_id", card.LedgerAccountId);
                    command.Parameters.AddWithValue(
                        "transaction_id",
                        card.IssuanceLedgerTransactionId);
                }));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.accounts
                where id = @id
                  and type = 'GiftCardValue'
                  and gift_card_id = @card_id
                """,
                command =>
                {
                    command.Parameters.AddWithValue("id", card.LedgerAccountId);
                    command.Parameters.AddWithValue("card_id", card.Id);
                }));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.entries
                where transaction_id = @transaction_id
                """,
                command => command.Parameters.AddWithValue(
                    "transaction_id",
                    card.IssuanceLedgerTransactionId)));
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                """
                select count(*)
                from (
                    select currency
                    from ledger.entries
                    where transaction_id = @transaction_id
                    group by currency
                    having sum(
                        case direction
                            when 'Credit' then amount
                            else -amount
                        end) <> 0
                ) imbalance
                """,
                command => command.Parameters.AddWithValue(
                    "transaction_id",
                    card.IssuanceLedgerTransactionId)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where operation = 'gift_card.issued'
                  and entity_id = @entity_id
                  and actor_membership_id is not null
                """,
                command => command.Parameters.AddWithValue(
                    "entity_id",
                    card.Id.ToString())));
        Assert.Equal(750m, await CorporateBalanceAsync(session, rootId));
        Assert.Equal(250m, await CardBalanceAsync(session, card.LedgerAccountId));
    }

    [Fact]
    public async Task Validity_defaults_to_server_posting_time_and_expiration_is_required()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var request = NewRequest(25m);

        var response = await IssueClient(rootId).PostAsJsonAsync(Route(rootId), request);
        response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<CardResponse>())!;

        Assert.Equal(card.IssuedAtUtc, card.ValidFromUtc);
        var expectedExpiry = request.ExpiresAtUtc!.Value.ToUniversalTime();
        expectedExpiry = new DateTimeOffset(
            expectedExpiry.Ticks - (expectedExpiry.Ticks % 10),
            TimeSpan.Zero);
        Assert.Equal(expectedExpiry, card.ExpiresAtUtc);

        var invalid = await IssueClient(rootId).PostAsJsonAsync(
            Route(rootId),
            NewRequest(10m) with
            {
                ExpiresAtUtc = null,
                IdempotencyKey = "gift-card-" + Guid.NewGuid().ToString("N"),
            });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Identical_retry_returns_the_original_without_duplicate_value()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var request = NewRequest(40m);
        var client = IssueClient(rootId);

        var first = await client.PostAsJsonAsync(Route(rootId), request);
        var second = await client.PostAsJsonAsync(Route(rootId), request);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var firstCard = (await first.Content.ReadFromJsonAsync<CardResponse>())!;
        var secondCard = (await second.Content.ReadFromJsonAsync<CardResponse>())!;
        Assert.Equal(firstCard, secondCard);

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, rootId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.gift_cards
                where funding_organization_id = @root
                  and idempotency_key = @key
                """,
                command =>
                {
                    command.Parameters.AddWithValue("root", rootId);
                    command.Parameters.AddWithValue("key", request.IdempotencyKey);
                }));
        Assert.Equal(60m, await CorporateBalanceAsync(session, rootId));
    }

    [Fact]
    public async Task Reusing_a_key_for_changed_intent_is_a_conflict()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var request = NewRequest(25m);
        var client = IssueClient(rootId);
        (await client.PostAsJsonAsync(Route(rootId), request)).EnsureSuccessStatusCode();

        var conflict = await client.PostAsJsonAsync(
            Route(rootId),
            request with { Amount = request.Amount + 1m });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Issuance_requires_the_named_organization_permission()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var request = NewRequest(10m);

        var unauthenticated = await fixture.Factory
            .CreateClient()
            .PostAsJsonAsync(Route(rootId), request);
        var wrongPermission = await OrganizationMember(
                fixture,
                rootId,
                OrganizationPermissions.GiftCardsView)
            .PostAsJsonAsync(Route(rootId), request);
        var platform = await PlatformOperator(
                fixture,
                PlatformPermissions.CorporateCreditsAllocate)
            .PostAsJsonAsync(Route(rootId), request);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
    }

    [Fact]
    public async Task Insufficient_corporate_credit_creates_no_card_or_ledger_effect()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 20m);
        var request = NewRequest(25m);

        var response = await IssueClient(rootId).PostAsJsonAsync(Route(rootId), request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, rootId);
        Assert.Equal(0, await session.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where operation_type = 'gift_card.issuance'
                """));
        Assert.Equal(20m, await CorporateBalanceAsync(session, rootId));
    }

    [Fact]
    public async Task Concurrent_issuance_cannot_overspend_corporate_credit()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var client = IssueClient(rootId);
        var firstRequest = NewRequest(80m);
        var secondRequest = NewRequest(80m);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(Route(rootId), firstRequest),
            client.PostAsJsonAsync(Route(rootId), secondRequest));

        Assert.Equal(1, responses.Count(response => response.IsSuccessStatusCode));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, rootId);
        Assert.Equal(1, await session.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(20m, await CorporateBalanceAsync(session, rootId));
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                """
                select count(*)
                from (
                    select account.id,
                           coalesce(sum(
                               case entry.direction
                                   when 'Credit' then entry.amount
                                   else -entry.amount
                               end), 0) balance
                    from ledger.accounts account
                    left join ledger.entries entry on entry.account_id = account.id
                    where account.type in (
                        'OrganizationCorporateCredit',
                        'GiftCardValue')
                    group by account.id
                    having coalesce(sum(
                        case entry.direction
                            when 'Credit' then entry.amount
                            else -entry.amount
                        end), 0) < 0
                ) negative_account
                """));
    }

    [Fact]
    public async Task Subtree_permission_issues_for_a_descendant_from_the_root_funding_account()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var childId = await CreateSubsidiaryAsync(rootId);
        var actorUserId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(
            fixture,
            actorUserId,
            rootId,
            []);
        var role = await CreateRoleAsync(
            fixture,
            rootId,
            OrganizationPermissions.GiftCardsIssue,
            OrganizationPermissions.GiftCardsView);
        await AssignRoleAsync(
            fixture,
            rootId,
            membershipId,
            role.Id,
            RoleScope.Subtree,
            anchorOrganizationId: rootId);
        var actor = OrganizationMember(fixture, actorUserId, rootId);

        var response = await actor.PostAsJsonAsync(Route(childId), NewRequest(30m));

        response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<CardResponse>())!;
        Assert.Equal(rootId, card.FundingOrganizationId);
        Assert.Equal(childId, card.IssuingOrganizationId);
        Assert.Equal(childId, card.OwnerOrganizationId);

        var inventory = await actor.GetAsync(InventoryRoute(childId, limit: 20));
        inventory.EnsureSuccessStatusCode();
        var page = (await inventory.Content.ReadFromJsonAsync<InventoryPageResponse>())!;
        Assert.Contains(page.Items, item => item.Id == card.Id);
    }

    [Fact]
    public async Task Inventory_is_permission_checked_cursor_paged_and_tenant_isolated()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        var otherRootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        await FundAsync(otherRootId, 100m);
        var issue = IssueClient(rootId);
        var issued = new List<Guid>();
        foreach (var amount in new[] { 10m, 20m, 30m })
        {
            var response = await issue.PostAsJsonAsync(Route(rootId), NewRequest(amount));
            response.EnsureSuccessStatusCode();
            issued.Add((await response.Content.ReadFromJsonAsync<CardResponse>())!.Id);
        }

        (await IssueClient(otherRootId).PostAsJsonAsync(
            Route(otherRootId),
            NewRequest(15m))).EnsureSuccessStatusCode();

        var viewer = OrganizationMember(
            fixture,
            rootId,
            OrganizationPermissions.GiftCardsView);
        var firstResponse = await viewer.GetAsync(InventoryRoute(rootId, limit: 2));
        firstResponse.EnsureSuccessStatusCode();
        var first = (await firstResponse.Content.ReadFromJsonAsync<InventoryPageResponse>())!;
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);

        var secondResponse = await viewer.GetAsync(
            InventoryRoute(rootId, limit: 2, first.NextCursor));
        secondResponse.EnsureSuccessStatusCode();
        var second = (await secondResponse.Content.ReadFromJsonAsync<InventoryPageResponse>())!;
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(
            issued.Order(),
            first.Items.Concat(second.Items).Select(card => card.Id).Order());

        var denied = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                rootId,
                OrganizationPermissions.GiftCardsIssue)
            .GetAsync(InventoryRoute(rootId, limit: 20));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await using var ownSession =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, rootId);
        await using var otherSession =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, otherRootId);
        Assert.Equal(3, await ownSession.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(1, await otherSession.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
    }

    [Fact]
    public async Task Rls_supports_a_future_identity_owner_without_organization_membership()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var response = await IssueClient(rootId).PostAsJsonAsync(Route(rootId), NewRequest(10m));
        response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<CardResponse>())!;
        var ownerUserId = Guid.CreateVersion7();
        var invitationId = Guid.CreateVersion7();
        var claimedAtUtc = DateTimeOffset.UtcNow;

        await using (var platform = await ScopedSqlSession.OpenAsPlatformAsync(fixture))
        {
            await using var update = platform.Command(
                """
                update gift_cards.gift_cards
                set ownership_state = 'IdentityOwned',
                    lifecycle_state = 'Active',
                    owner_organization_id = null,
                    owner_user_id = @owner,
                    distribution_invitation_id = @invitation,
                    distributed_at_utc = @claimed_at,
                    claimed_at_utc = @claimed_at
                where id = @id
                """);
            update.Parameters.AddWithValue("owner", ownerUserId);
            update.Parameters.AddWithValue("invitation", invitationId);
            update.Parameters.AddWithValue("claimed_at", claimedAtUtc);
            update.Parameters.AddWithValue("id", card.Id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
            await platform.CommitAsync();
        }

        await using var owner =
            await ScopedSqlSession.OpenAsIdentityAsync(fixture, ownerUserId);
        await using var stranger =
            await ScopedSqlSession.OpenAsIdentityAsync(fixture, Guid.CreateVersion7());
        await using var noContext = await fixture.OpenAppConnectionAsync();
        await using var noContextCommand = new NpgsqlCommand(
            "select count(*) from gift_cards.gift_cards",
            noContext);

        Assert.Equal(1, await owner.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(0, await stranger.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(0L, (long)(await noContextCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Gift_card_rls_is_forced_and_runtime_cannot_delete_provenance()
    {
        var rootId = await CreateOrganizationAsync(fixture);
        await FundAsync(rootId, 100m);
        var response = await IssueClient(rootId).PostAsJsonAsync(Route(rootId), NewRequest(10m));
        response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<CardResponse>())!;

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, rootId);
        await using (var rls = session.Command(
                         """
                         select relrowsecurity, relforcerowsecurity
                         from pg_class
                         where oid = 'gift_cards.gift_cards'::regclass
                         """))
        await using (var reader = await rls.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }

        await using var delete = session.Command(
            "delete from gift_cards.gift_cards where id = @id");
        delete.Parameters.AddWithValue("id", card.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => delete.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
    }

    private async Task FundAsync(Guid organizationId, decimal amount)
    {
        var response = await PlatformOperator(
                fixture,
                PlatformPermissions.CorporateCreditsAllocate)
            .PostAsJsonAsync(
                "/api/v1/corporate-credits/allocations",
                new
                {
                    organizationId,
                    amount,
                    currency = "TRY",
                    businessReference = "FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "allocation-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> CreateSubsidiaryAsync(Guid rootId)
    {
        var response = await OrganizationMember(
                fixture,
                rootId,
                OrganizationPermissions.CreateSubsidiary)
            .PostAsJsonAsync(
                $"/api/v1/organizations/{rootId}/subsidiaries",
                new
                {
                    name = "Gift Card Issuing Department",
                    code = "GCI" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private HttpClient IssueClient(Guid organizationId) =>
        OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsIssue,
            OrganizationPermissions.GiftCardsView);

    private static string Route(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/";

    private static string InventoryRoute(
        Guid organizationId,
        int limit,
        string? cursor = null) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/inventory?limit={limit}" +
        (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));

    private static CardRequest NewRequest(decimal amount) =>
        new(
            amount,
            "TRY",
            ValidFromUtc: null,
            DateTimeOffset.UtcNow.AddYears(1),
            IsTransferable: null,
            IsDivisible: null,
            "AWARD-" + Guid.NewGuid().ToString("N"),
            "gift-card-" + Guid.NewGuid().ToString("N"));

    private static async Task<decimal> CorporateBalanceAsync(
        ScopedSqlSession session,
        Guid rootId)
    {
        await using var command = session.Command(
            """
            select coalesce(sum(
                case entry.direction
                    when 'Credit' then entry.amount
                    else -entry.amount
                end), 0)
            from ledger.accounts account
            left join ledger.entries entry on entry.account_id = account.id
            where account.organization_id = @root
              and account.type = 'OrganizationCorporateCredit'
              and account.currency = 'TRY'
            """);
        command.Parameters.AddWithValue("root", rootId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<decimal> CardBalanceAsync(
        ScopedSqlSession session,
        Guid accountId)
    {
        await using var command = session.Command(
            """
            select coalesce(sum(
                case direction
                    when 'Credit' then amount
                    else -amount
                end), 0)
            from ledger.entries
            where account_id = @account
            """);
        command.Parameters.AddWithValue("account", accountId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private sealed record CardRequest(
        decimal Amount,
        string Currency,
        DateTimeOffset? ValidFromUtc,
        DateTimeOffset? ExpiresAtUtc,
        bool? IsTransferable,
        bool? IsDivisible,
        string BusinessReference,
        string IdempotencyKey);

    private sealed record CardResponse(
        Guid Id,
        string PublicReference,
        Guid FundingOrganizationId,
        Guid IssuingOrganizationId,
        Guid? OwnerOrganizationId,
        Guid? OwnerUserId,
        string OwnershipState,
        string LifecycleState,
        Guid LedgerAccountId,
        Guid IssuanceLedgerTransactionId,
        decimal FundedAmount,
        string Currency,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset ExpiresAtUtc,
        bool IsTransferable,
        bool IsDivisible,
        Guid? SourceGiftCardId,
        Guid RootGiftCardId,
        int Generation,
        string BusinessReference,
        string IdempotencyKey,
        Guid IssuedByUserId,
        Guid IssuedByMembershipId,
        DateTimeOffset IssuedAtUtc);

    private sealed record InventoryPageResponse(
        IReadOnlyList<CardResponse> Items,
        int Limit,
        string? NextCursor);

    private sealed record IdResponse(Guid Id);
}
