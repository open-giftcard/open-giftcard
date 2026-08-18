using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class ReportingTests(PlatformApiFixture fixture)
{
    private const string RecipientPassword = "recipient reporting passphrase";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Complete_story_has_exact_summary_history_owner_views_and_reconciliation()
    {
        var story = await CreateCompleteStoryAsync();
        await SuspendOrganizationAsync(story.OrganizationId);
        var financial = FinancialClient(story.OrganizationId);

        var summary = await financial.GetFromJsonAsync<OrganizationFinancialSummary>(
            SummaryRoute(story.OrganizationId));
        Assert.NotNull(summary);
        var totals = Assert.Single(summary.Currencies);
        Assert.Equal("TRY", totals.Currency);
        Assert.Equal(1050m, totals.Granted);
        Assert.Equal(50m, totals.Reversed);
        Assert.Equal(150m, totals.Issued);
        Assert.Equal(100m, totals.Distributed);
        Assert.Equal(900m, totals.RemainingCorporateCredit);
        Assert.Equal(100m, totals.RemainingGiftCardValue);
        Assert.Equal(50m, totals.CancelledReturned);
        Assert.Equal(0m, totals.ExpiredReturned);

        var history = await financial.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(story.OrganizationId));
        Assert.NotNull(history);
        Assert.Equal(10, history.Items.Count);
        Assert.Contains(
            history.Items,
            item => item.Category == "CorporateCredit" &&
                item.Operation == "Allocated");
        Assert.Contains(
            history.Items,
            item => item.Operation == "Reversed" &&
                item.Amount == 50m);
        Assert.Contains(
            history.Items,
            item => item.Operation == "Claimed" &&
                item.GiftCardId == story.OwnedCard.Id);
        Assert.Contains(
            history.Items,
            item => item.Operation == "Cancel" &&
                item.Amount == 50m &&
                item.FinancialDirection == "Credit");
        Assert.Equal(
            history.Items.Count,
            history.Items.Select(item => item.EventKey).Distinct().Count());
        Assert.True(
            history.Items
                .Zip(history.Items.Skip(1))
                .All(pair =>
                    pair.First.OccurredAtUtc > pair.Second.OccurredAtUtc ||
                    (pair.First.OccurredAtUtc == pair.Second.OccurredAtUtc &&
                     string.CompareOrdinal(
                         pair.First.EventKey,
                         pair.Second.EventKey) > 0)));

        var pagedHistory = new List<FinancialHistoryItem>();
        string? historyCursor = null;
        do
        {
            var cursorQuery = historyCursor is null
                ? string.Empty
                : "&cursor=" + Uri.EscapeDataString(historyCursor);
            var page = await financial.GetFromJsonAsync<FinancialHistoryPage>(
                HistoryRoute(story.OrganizationId) + "?limit=3" + cursorQuery);
            Assert.NotNull(page);
            pagedHistory.AddRange(page.Items);
            historyCursor = page.NextCursor;
        }
        while (historyCursor is not null);
        Assert.Equal(
            history.Items.Select(item => item.EventKey),
            pagedHistory.Select(item => item.EventKey));

        var reconciliation =
            await financial.GetFromJsonAsync<OrganizationReconciliationResult>(
                ReconciliationRoute(story.OrganizationId),
                JsonOptions);
        Assert.NotNull(reconciliation);
        Assert.True(reconciliation.IsConsistent);
        Assert.Empty(reconciliation.Findings);
        Assert.Equal(6, reconciliation.TransactionsChecked);
        Assert.Equal(2, reconciliation.GiftCardsChecked);

        var owner = IdentityClient(story.OwnerUserId);
        var cards = await owner.GetFromJsonAsync<OwnedGiftCardPage>(
            "/api/v1/me/gift-cards");
        Assert.NotNull(cards);
        var owned = Assert.Single(cards.Items);
        Assert.Equal(story.OwnedCard.Id, owned.Id);
        Assert.Equal(100m, owned.Balance);
        Assert.Equal("Active", owned.LifecycleState);

        var detail = await owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{story.OwnedCard.Id}");
        Assert.NotNull(detail);
        Assert.Equal(story.OwnedCard.PublicReference, detail.PublicReference);
        Assert.Equal(100m, detail.Balance);
        Assert.Equal("IdentityOwned", detail.OwnershipState);

        var first = await owner.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/me/gift-cards/{story.OwnedCard.Id}/history?limit=2");
        Assert.NotNull(first);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = await owner.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/me/gift-cards/{story.OwnedCard.Id}/history?limit=2&" +
            $"cursor={Uri.EscapeDataString(first.NextCursor)}");
        Assert.NotNull(second);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(
            first.Items.Select(item => item.EventKey)
                .Intersect(second.Items.Select(item => item.EventKey)));

        var allOwnerHistory = await owner.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/me/gift-cards/{story.OwnedCard.Id}/history");
        Assert.NotNull(allOwnerHistory);
        Assert.Equal(5, allOwnerHistory.Items.Count);
        Assert.Contains(
            allOwnerHistory.Items,
            item => item.Category == "Ledger" &&
                item.Operation == "gift_card.issuance" &&
                item.Amount == 100m);
        Assert.Contains(
            allOwnerHistory.Items,
            item => item.Category == "Lifecycle" &&
                item.Operation == "Suspend");
    }

    [Fact]
    public async Task Organization_history_filters_are_authoritative_literal_and_time_bounded()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        const string literalReference = @"SPECIAL%_REF\Q3";
        _ = await AllocateAsync(organizationId, 100m, literalReference);
        _ = await AllocateAsync(organizationId, 75m, "SPECIALxxREF-Q3");
        var client = FinancialClient(organizationId);

        var literal = await client.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(organizationId) +
            "?category=corporatecredit&operation=allocated&currency=try&reference=" +
            Uri.EscapeDataString(literalReference.ToLowerInvariant()));
        Assert.NotNull(literal);
        var matching = Assert.Single(literal.Items);
        Assert.Equal(literalReference, matching.BusinessReference);
        Assert.Equal("CorporateCredit", matching.Category);
        Assert.Equal("Allocated", matching.Operation);
        Assert.Equal("TRY", matching.Currency);

        var boundary = Uri.EscapeDataString(
            matching.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        var inclusive = await client.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(organizationId) +
            "?reference=" + Uri.EscapeDataString(literalReference) +
            "&occurredFromUtc=" + boundary);
        Assert.NotNull(inclusive);
        Assert.Equal(matching.EventKey, Assert.Single(inclusive.Items).EventKey);

        var exclusive = await client.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(organizationId) +
            "?reference=" + Uri.EscapeDataString(literalReference) +
            "&occurredBeforeUtc=" + boundary);
        Assert.NotNull(exclusive);
        Assert.Empty(exclusive.Items);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync(
                HistoryRoute(organizationId) +
                "?occurredFromUtc=" + boundary +
                "&occurredBeforeUtc=" + boundary)).StatusCode);
    }

    [Fact]
    public async Task Filtered_history_cursor_is_stable_and_bound_to_normalized_filters()
    {
        var story = await CreateCompleteStoryAsync();
        var client = FinancialClient(story.OrganizationId);
        var first = await client.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(story.OrganizationId) +
            "?category=lifecycle&limit=1");
        Assert.NotNull(first);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);

        var second = await client.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(story.OrganizationId) +
            "?category=LIFECYCLE&limit=1&cursor=" +
            Uri.EscapeDataString(first.NextCursor));
        Assert.NotNull(second);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].EventKey, second.Items[0].EventKey);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync(
                HistoryRoute(story.OrganizationId) +
                "?category=distribution&limit=1&cursor=" +
                Uri.EscapeDataString(first.NextCursor))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync(
                HistoryRoute(story.OrganizationId) +
                "?limit=1&cursor=" +
                Uri.EscapeDataString(first.NextCursor))).StatusCode);
    }

    [Fact]
    public async Task Development_OpenAPI_exposes_financial_history_search_parameters()
    {
        var response = await fixture.Factory.CreateClient()
            .GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty(
                "/api/v1/organizations/{organizationId}/reports/financial-history")
            .GetProperty("get");
        var parameterNames = operation
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("category", parameterNames);
        Assert.Contains("operation", parameterNames);
        Assert.Contains("currency", parameterNames);
        Assert.Contains("reference", parameterNames);
        Assert.Contains("occurredFromUtc", parameterNames);
        Assert.Contains("occurredBeforeUtc", parameterNames);

        var shareParameters = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/me/shares")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("kind", shareParameters);
        Assert.Contains("state", shareParameters);
        Assert.Contains("direction", shareParameters);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var reconciliation = schemas
            .GetProperty("OrganizationReconciliationResult")
            .GetProperty("properties");
        Assert.True(reconciliation.TryGetProperty("sharesChecked", out _));
        Assert.True(reconciliation.TryGetProperty("activeReservationsChecked", out _));
        var share = schemas.GetProperty("GiftCardShareResult").GetProperty("properties");
        Assert.True(share.TryGetProperty("sourceGiftCardPublicReference", out _));
        Assert.True(share.TryGetProperty("childGiftCardPublicReference", out _));
        Assert.False(share.TryGetProperty("recipientContact", out _));
        Assert.False(share.TryGetProperty("claimSecretHash", out _));
    }

    [Fact]
    public async Task Reporting_permissions_tenant_boundaries_and_owner_ledger_rls_fail_closed()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 300m);
        var card = await IssueAsync(organizationId, 100m);
        var claim = await DistributeAndClaimAsync(organizationId, card.Id);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);

        var onlyCorporate = OrganizationMember(
            fixture,
            Guid.CreateVersion7(),
            organizationId,
            OrganizationPermissions.CorporateCreditsView);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await onlyCorporate.GetAsync(SummaryRoute(organizationId))).StatusCode);

        var onlyCards = OrganizationMember(
            fixture,
            Guid.CreateVersion7(),
            organizationId,
            OrganizationPermissions.GiftCardsView);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await onlyCards.GetAsync(SummaryRoute(organizationId))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await FinancialClient(organizationId)
                .GetAsync(SummaryRoute(otherOrganizationId))).StatusCode);

        var subsidiaryResponse = await OrganizationMember(
                fixture,
                organizationId,
                OrganizationPermissions.CreateSubsidiary)
            .PostAsJsonAsync(
                $"/api/v1/organizations/{organizationId}/subsidiaries",
                new
                {
                    name = "Reporting Scope",
                    code = "RPT-" + Guid.NewGuid().ToString("N")[..10],
                });
        subsidiaryResponse.EnsureSuccessStatusCode();
        var subsidiary =
            await subsidiaryResponse.Content.ReadFromJsonAsync<OrganizationIdResponse>();
        Assert.NotNull(subsidiary);
        var scopedUserId = Guid.CreateVersion7();
        var scopedMembershipId = await ProvisionOrganizationActorAsync(
            fixture,
            scopedUserId,
            organizationId,
            []);
        var scopedRole = await AuthorizationTestSupport.CreateRoleAsync(
            fixture,
            organizationId,
            OrganizationPermissions.CorporateCreditsView,
            OrganizationPermissions.GiftCardsView);
        _ = await AuthorizationTestSupport.AssignRoleAsync(
            fixture,
            organizationId,
            scopedMembershipId,
            scopedRole.Id,
            RoleScope.SelectedOrganizations,
            organizationId,
            [subsidiary.Id]);
        var subsidiaryScoped = IdentityClient(scopedUserId);
        subsidiaryScoped.DefaultRequestHeaders.Add(
            OrganizationIdHeader,
            organizationId.ToString());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await subsidiaryScoped.GetAsync(SummaryRoute(organizationId))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PlatformOperator(
                    fixture,
                    PlatformPermissions.CorporateCreditsView)
                .GetAsync(SummaryRoute(organizationId))).StatusCode);

        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.CorporateCreditsView,
            PlatformPermissions.GiftCardsView);
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.GetAsync(SummaryRoute(organizationId))).StatusCode);

        var owner = IdentityClient(claim.OwnerUserId);
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.GetAsync($"/api/v1/me/gift-cards/{card.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await IdentityClient(Guid.CreateVersion7())
                .GetAsync($"/api/v1/me/gift-cards/{card.Id}")).StatusCode);

        await using (var ownerSession =
            await ScopedSqlSession.OpenAsIdentityAsync(fixture, claim.OwnerUserId))
        {
            Assert.Equal(
                1,
                await ownerSession.ScalarCountAsync(
                    "select count(*) from ledger.accounts where gift_card_id = @id",
                    command => command.Parameters.AddWithValue("id", card.Id)));
            Assert.Equal(
                1,
                await ownerSession.ScalarCountAsync(
                    """
                    select count(distinct ledger_transaction.id)
                    from ledger.transactions ledger_transaction
                    join ledger.entries entry
                      on entry.transaction_id = ledger_transaction.id
                    join ledger.accounts account on account.id = entry.account_id
                    where account.gift_card_id = @id
                    """,
                    command => command.Parameters.AddWithValue("id", card.Id)));
        }

        await using (var otherOwnerSession =
            await ScopedSqlSession.OpenAsIdentityAsync(
                fixture,
                Guid.CreateVersion7()))
        {
            Assert.Equal(
                0,
                await otherOwnerSession.ScalarCountAsync(
                    "select count(*) from ledger.accounts where gift_card_id = @id",
                    command => command.Parameters.AddWithValue("id", card.Id)));
        }

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var contextFree = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from ledger.accounts",
            connection,
            contextFree);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Reconciliation_identifies_balanced_orphan_without_mutating_history()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 200m);
        var orphanId = await InsertBalancedOrphanIssuanceAsync(organizationId);

        var report = await FinancialClient(organizationId)
            .GetFromJsonAsync<OrganizationReconciliationResult>(
                ReconciliationRoute(organizationId),
                JsonOptions);
        Assert.NotNull(report);
        Assert.False(report.IsConsistent);
        Assert.Contains(
            report.Findings,
            finding =>
                finding.Code == "ledger.transaction.orphan" &&
                finding.EntityId == orphanId.ToString());
        Assert.Contains(
            report.Findings,
            finding =>
                finding.Code == "organization.corporate_balance.mismatch" &&
                finding.ExpectedAmount == 200m &&
                finding.ActualAmount == 199m);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from ledger.transactions where id = @id",
                command => command.Parameters.AddWithValue("id", orphanId)));
    }

    [Fact]
    public async Task Audit_investigation_requires_named_permission_and_filters_safely()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var denied = await OrganizationMember(
                fixture,
                organizationId,
                OrganizationPermissions.GiftCardsView)
            .GetAsync(SummaryRoute(organizationId));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var denialCorrelation = await CorrelationIdFromAsync(denied);

        var noAuditPermission = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsView);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await noAuditPermission.GetAsync(
                AuditRoute(organizationId))).StatusCode);

        var investigator = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.AuditView);
        var filtered = await investigator.GetFromJsonAsync<AuditInvestigationPage>(
            AuditRoute(organizationId) +
            "?operation=authorization.denied&outcome=Failure&" +
            $"correlationId={denialCorrelation}",
            JsonOptions);
        Assert.NotNull(filtered);
        var denial = Assert.Single(filtered.Items);
        Assert.Equal(AuditOutcome.Failure, denial.Outcome);
        Assert.Equal(denialCorrelation, denial.CorrelationId);
        Assert.Equal("authorization.denied", denial.Operation);
        Assert.DoesNotContain(
            denial.Metadata.Values,
            value => value.Contains("Bearer", StringComparison.OrdinalIgnoreCase));

        var first = await investigator.GetFromJsonAsync<AuditInvestigationPage>(
            AuditRoute(organizationId) + "?limit=1",
            JsonOptions);
        Assert.NotNull(first);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        var second = await investigator.GetFromJsonAsync<AuditInvestigationPage>(
            AuditRoute(organizationId) + "?limit=1&cursor=" +
            Uri.EscapeDataString(first.NextCursor),
            JsonOptions);
        Assert.NotNull(second);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await investigator.GetAsync(
                AuditRoute(organizationId) + "?cursor=invalid")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await investigator.GetAsync(
                AuditRoute(organizationId) + "?outcome=999")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await investigator.GetAsync(
                AuditRoute(otherOrganizationId))).StatusCode);

        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.AuditView);
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.GetAsync(AuditRoute(organizationId))).StatusCode);
    }

    [Fact]
    public async Task Reporting_cursors_and_hidden_cards_fail_safely()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 300m);
        var firstCard = await IssueAsync(organizationId, 50m);
        var sharedRecipient = $"report-shared-{Guid.NewGuid():N}@example.com";
        var firstClaim = await DistributeAndClaimAsync(
            organizationId,
            firstCard.Id,
            sharedRecipient);
        var secondCard = await IssueAsync(organizationId, 75m);
        var secondClaim = await DistributeAndClaimAsync(
            organizationId,
            secondCard.Id,
            sharedRecipient);
        Assert.Equal(firstClaim.OwnerUserId, secondClaim.OwnerUserId);
        var owner = IdentityClient(firstClaim.OwnerUserId);

        var firstPage = await owner.GetFromJsonAsync<OwnedGiftCardPage>(
            "/api/v1/me/gift-cards?limit=1");
        Assert.NotNull(firstPage);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await owner.GetFromJsonAsync<OwnedGiftCardPage>(
            "/api/v1/me/gift-cards?limit=1&cursor=" +
            Uri.EscapeDataString(firstPage.NextCursor));
        Assert.NotNull(secondPage);
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items[0].Id, secondPage.Items[0].Id);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.GetAsync("/api/v1/me/gift-cards?cursor=invalid"))
                .StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await FinancialClient(organizationId).GetAsync(
                HistoryRoute(organizationId) + "?limit=201")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.GetAsync(
                $"/api/v1/me/gift-cards/{Guid.CreateVersion7()}"))
                .StatusCode);
    }

    [Fact]
    public async Task Sharing_history_and_reconciliation_are_authoritative_masked_and_filter_safe()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 250m);
        var source = await IssueAsync(organizationId, 100m, shareable: true);
        var recipientSeed = await IssueAsync(organizationId, 10m);
        var senderUserId = (await DistributeAndClaimAsync(
            organizationId,
            source.Id,
            $"report-share-sender-{Guid.NewGuid():N}@example.com")).OwnerUserId;
        var recipientUserId = (await DistributeAndClaimAsync(
            organizationId,
            recipientSeed.Id,
            $"report-share-recipient-{Guid.NewGuid():N}@example.com")).OwnerUserId;
        using var sender = IdentityClient(senderUserId);
        using var recipient = IdentityClient(recipientUserId);

        var protectedResponse = await sender.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{source.Id}/shares",
            new
            {
                amount = 25m,
                idempotencyKey = "report-sharing-protected-" + Guid.NewGuid().ToString("N"),
            });
        protectedResponse.EnsureSuccessStatusCode();
        var protectedShare = (await protectedResponse.Content
            .ReadFromJsonAsync<CreatedGiftCardShareResult>(JsonOptions))!;
        var protectedClaim = await recipient.PostAsJsonAsync(
            "/api/v1/share-claims",
            new
            {
                claimToken = ExtractQueryValue(protectedShare.ClaimUrl, "token"),
                pin = protectedShare.Pin,
                idempotencyKey = "report-sharing-claim-" + Guid.NewGuid().ToString("N"),
            });
        protectedClaim.EnsureSuccessStatusCode();

        var rawContact = $"report-direct-{Guid.NewGuid():N}@example.com";
        var directResponse = await sender.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{source.Id}/share-invitations",
            new
            {
                amount = 5m,
                contactType = "Email",
                recipientContact = rawContact,
                idempotencyKey = "report-sharing-direct-" + Guid.NewGuid().ToString("N"),
            });
        directResponse.EnsureSuccessStatusCode();
        var directShare = (await directResponse.Content
            .ReadFromJsonAsync<CreatedDirectGiftCardShareResult>(JsonOptions))!;
        var cancel = await sender.PostAsJsonAsync(
            $"/api/v1/me/shares/{directShare.Share.Id}/cancel",
            new
            {
                idempotencyKey = "report-sharing-cancel-" + Guid.NewGuid().ToString("N"),
            });
        cancel.EnsureSuccessStatusCode();

        var financial = FinancialClient(organizationId);
        var summary = await financial.GetFromJsonAsync<OrganizationFinancialSummary>(
            SummaryRoute(organizationId));
        Assert.NotNull(summary);
        var totals = Assert.Single(summary.Currencies);
        Assert.Equal(110m, totals.Issued);
        Assert.Equal(110m, totals.RemainingGiftCardValue);

        var history = await financial.GetFromJsonAsync<FinancialHistoryPage>(
            HistoryRoute(organizationId) + "?category=Sharing");
        Assert.NotNull(history);
        Assert.Equal(4, history.Items.Count);
        Assert.All(history.Items, item => Assert.Equal("Sharing", item.Category));
        Assert.Contains(history.Items, item =>
            item.Operation == "Claimed" &&
            item.FinancialDirection == "Transferred" &&
            item.GiftCardPublicReference == source.PublicReference);
        Assert.Contains(history.Items, item =>
            item.Operation == "Cancelled" &&
            item.FinancialDirection == "Released" &&
            item.BusinessReference == $"DirectInvitation:{directShare.MaskedRecipientContact}");
        Assert.DoesNotContain(
            rawContact,
            JsonSerializer.Serialize(history, JsonOptions),
            StringComparison.OrdinalIgnoreCase);

        var reconciliation = await financial
            .GetFromJsonAsync<OrganizationReconciliationResult>(
                ReconciliationRoute(organizationId),
                JsonOptions);
        Assert.NotNull(reconciliation);
        Assert.True(reconciliation.IsConsistent);
        Assert.Empty(reconciliation.Findings);
        Assert.Equal(2, reconciliation.SharesChecked);
        Assert.Equal(0, reconciliation.ActiveReservationsChecked);

        var sentPage = await sender.GetFromJsonAsync<GiftCardSharePage>(
            "/api/v1/me/shares?limit=1&direction=Sent",
            JsonOptions);
        Assert.NotNull(sentPage);
        var sent = Assert.Single(sentPage.Items);
        Assert.Equal(source.PublicReference, sent.SourceGiftCardPublicReference);
        Assert.NotNull(sentPage.NextCursor);
        var mismatchedCursor = await sender.GetAsync(
            "/api/v1/me/shares?limit=1&direction=Received&cursor=" +
            Uri.EscapeDataString(sentPage.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedCursor.StatusCode);

        var receivedPage = await recipient.GetFromJsonAsync<GiftCardSharePage>(
            "/api/v1/me/shares?direction=Received&state=Claimed&kind=ProtectedLink",
            JsonOptions);
        Assert.NotNull(receivedPage);
        var received = Assert.Single(receivedPage.Items);
        Assert.Equal(protectedShare.Share.Id, received.Id);
        Assert.NotNull(received.ChildGiftCardPublicReference);
    }

    [Fact]
    public async Task Sharing_reconciliation_reports_incomplete_claims_and_orphaned_reservations()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 100m);
        var source = await IssueAsync(organizationId, 50m, shareable: true);
        var senderUserId = (await DistributeAndClaimAsync(
            organizationId,
            source.Id,
            $"report-broken-share-{Guid.NewGuid():N}@example.com")).OwnerUserId;
        using var sender = IdentityClient(senderUserId);
        var pending = await sender.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{source.Id}/shares",
            new
            {
                amount = 5m,
                idempotencyKey = "report-pending-share-" + Guid.NewGuid().ToString("N"),
            });
        pending.EnsureSuccessStatusCode();

        var brokenShareId = await InsertBrokenClaimAndSuspendSourceAsync(
            organizationId,
            source.Id,
            senderUserId);
        var reconciliation = await FinancialClient(organizationId)
            .GetFromJsonAsync<OrganizationReconciliationResult>(
                ReconciliationRoute(organizationId),
                JsonOptions);
        Assert.NotNull(reconciliation);
        Assert.False(reconciliation.IsConsistent);
        Assert.Equal(2, reconciliation.SharesChecked);
        Assert.Equal(1, reconciliation.ActiveReservationsChecked);
        Assert.Contains(reconciliation.Findings, finding =>
            finding.Code == "sharing.claimed_without_transfer" &&
            finding.EntityId == brokenShareId.ToString());
        Assert.Contains(reconciliation.Findings, finding =>
            finding.Code == "sharing.reservation.source_invalid" &&
            finding.EntityId != brokenShareId.ToString());
    }

    private async Task<CompleteStory> CreateCompleteStoryAsync()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        _ = await AllocateAsync(organizationId, 1000m);
        var reversible = await AllocateAsync(organizationId, 50m);
        await ReverseAsync(reversible.Id);

        var ownedCard = await IssueAsync(organizationId, 100m);
        var claim = await DistributeAndClaimAsync(
            organizationId,
            ownedCard.Id);
        var owner = IdentityClient(claim.OwnerUserId);
        var suspend = await owner.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{ownedCard.Id}/lifecycle/suspend",
            OwnerLifecycleRequest());
        suspend.EnsureSuccessStatusCode();
        var reactivate = await owner.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{ownedCard.Id}/lifecycle/reactivate",
            OwnerLifecycleRequest());
        reactivate.EnsureSuccessStatusCode();

        var cancelledCard = await IssueAsync(organizationId, 50m);
        var cancellation = await OrganizationMember(
                fixture,
                organizationId,
                OrganizationPermissions.GiftCardsManageLifecycle,
                OrganizationPermissions.GiftCardsView)
            .PostAsJsonAsync(
                $"/api/v1/organizations/{organizationId}/gift-cards/" +
                $"{cancelledCard.Id}/lifecycle/cancel",
                new
                {
                    reason = "Award withdrawn before delivery.",
                    idempotencyKey =
                        "report-cancel-" + Guid.NewGuid().ToString("N"),
                });
        cancellation.EnsureSuccessStatusCode();
        return new CompleteStory(
            organizationId,
            ownedCard,
            claim.OwnerUserId);
    }

    private async Task<AllocationResponse> AllocateAsync(
        Guid organizationId,
        decimal amount,
        string? businessReference = null)
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
                    businessReference = businessReference ??
                        "REPORT-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey =
                        "report-fund-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AllocationResponse>())!;
    }

    private async Task ReverseAsync(Guid allocationId)
    {
        var response = await PlatformOperator(
                fixture,
                PlatformPermissions.CorporateCreditsReverse)
            .PostAsJsonAsync(
                $"/api/v1/corporate-credits/allocations/{allocationId}/reversal",
                new
                {
                    reason = "Reconciliation test correction.",
                    idempotencyKey =
                        "report-reversal-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private async Task<CardResponse> IssueAsync(
        Guid organizationId,
        decimal amount,
        bool shareable = false)
    {
        var response = await OrganizationMember(
                fixture,
                organizationId,
                OrganizationPermissions.GiftCardsIssue,
                OrganizationPermissions.GiftCardsView)
            .PostAsJsonAsync(
                $"/api/v1/organizations/{organizationId}/gift-cards/",
                new
                {
                    amount,
                    currency = "TRY",
                    isTransferable = shareable,
                    isDivisible = shareable,
                    expiresAtUtc = DateTimeOffset.UtcNow.AddYears(1),
                    businessReference =
                        "REPORT-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey =
                        "report-card-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CardResponse>())!;
    }

    private async Task<ClaimResponse> DistributeAndClaimAsync(
        Guid organizationId,
        Guid giftCardId,
        string? recipientContact = null)
    {
        var distributor = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsDistribute);
        var distribution = await distributor.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/gift-cards/" +
            $"{giftCardId}/distributions/",
            new
            {
                contactType = "Email",
                recipientContact = recipientContact ??
                    $"report-{Guid.NewGuid():N}@example.com",
                businessReference =
                    "REPORT-DIST-" + Guid.NewGuid().ToString("N"),
                idempotencyKey =
                    "report-dist-" + Guid.NewGuid().ToString("N"),
            });
        distribution.EnsureSuccessStatusCode();
        var invitation =
            (await distribution.Content.ReadFromJsonAsync<InvitationResponse>())!;
        var delivery = await distributor.GetFromJsonAsync<DeliveryResponse>(
            $"/api/v1/development/organizations/{organizationId}/" +
            $"claim-deliveries/{invitation.Id}");
        Assert.NotNull(delivery);
        const string tokenMarker = "token=";
        var tokenIndex = delivery.ClaimUrl.IndexOf(
            tokenMarker,
            StringComparison.Ordinal);
        Assert.True(tokenIndex >= 0);
        var token = Uri.UnescapeDataString(
            delivery.ClaimUrl[(tokenIndex + tokenMarker.Length)..]);

        var claim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken = token,
                password = RecipientPassword,
                idempotencyKey =
                    "report-claim-" + Guid.NewGuid().ToString("N"),
            });
        claim.EnsureSuccessStatusCode();
        return (await claim.Content.ReadFromJsonAsync<ClaimResponse>())!;
    }

    private async Task<Guid> InsertBalancedOrphanIssuanceAsync(
        Guid organizationId)
    {
        await using var connection =
            new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(
            connection,
            transaction,
            organizationId,
            isPlatformOperator: true);

        Guid corporateAccountId;
        Guid platformAccountId;
        await using (var accounts = new NpgsqlCommand(
            """
            select
                (
                    select id
                    from ledger.accounts
                    where type = 'OrganizationCorporateCredit'
                      and organization_id = @organization_id
                      and currency = 'TRY'
                    limit 1
                ),
                (
                    select id
                    from ledger.accounts
                    where type = 'PlatformFunding'
                      and organization_id is null
                      and currency = 'TRY'
                    limit 1
                )
            """,
            connection,
            transaction))
        {
            accounts.Parameters.AddWithValue("organization_id", organizationId);
            await using var reader = await accounts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            corporateAccountId = reader.GetGuid(0);
            platformAccountId = reader.GetGuid(1);
        }

        var transactionId = Guid.CreateVersion7();
        await using var insert = new NpgsqlCommand(
            """
            insert into ledger.transactions (
                id,
                organization_id,
                operation_type,
                business_reference,
                idempotency_key,
                intent_hash,
                reverses_transaction_id,
                initiated_by_user_id,
                posted_at_utc)
            values (
                @transaction_id,
                @organization_id,
                'gift_card.issuance',
                'TEST-ORPHAN-ISSUANCE',
                @idempotency_key,
                @intent_hash,
                null,
                @actor_id,
                now());

            insert into ledger.entries (
                id,
                transaction_id,
                organization_id,
                account_id,
                direction,
                amount,
                currency)
            values
                (
                    @debit_id,
                    @transaction_id,
                    @organization_id,
                    @corporate_account_id,
                    'Debit',
                    1,
                    'TRY'),
                (
                    @credit_id,
                    @transaction_id,
                    @organization_id,
                    @platform_account_id,
                    'Credit',
                    1,
                    'TRY');
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("transaction_id", transactionId);
        insert.Parameters.AddWithValue("organization_id", organizationId);
        insert.Parameters.AddWithValue(
            "idempotency_key",
            "orphan-" + Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("intent_hash", new string('0', 64));
        insert.Parameters.AddWithValue("actor_id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("debit_id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("credit_id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue(
            "corporate_account_id",
            corporateAccountId);
        insert.Parameters.AddWithValue("platform_account_id", platformAccountId);
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return transactionId;
    }

    private async Task<Guid> InsertBrokenClaimAndSuspendSourceAsync(
        Guid organizationId,
        Guid sourceGiftCardId,
        Guid senderUserId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(
            connection,
            transaction,
            organizationId,
            isPlatformOperator: false,
            senderUserId);
        var shareId = Guid.CreateVersion7();
        await using var command = new NpgsqlCommand(
            """
            insert into sharing.shares (
                id,
                kind,
                source_gift_card_id,
                funding_organization_id,
                sender_user_id,
                claimed_by_user_id,
                child_gift_card_id,
                ledger_transaction_id,
                amount,
                currency,
                claim_secret_hash,
                pin_hash,
                recipient_contact_type,
                recipient_contact,
                masked_recipient_contact,
                identity_was_created_on_claim,
                state,
                failed_pin_attempts,
                create_idempotency_key,
                claim_idempotency_key,
                cancel_idempotency_key,
                created_at_utc,
                expires_at_utc,
                claimed_at_utc,
                closed_at_utc)
            values (
                @share_id,
                'ProtectedLink',
                @source_id,
                @organization_id,
                @sender_id,
                @recipient_id,
                @child_id,
                @transaction_id,
                1,
                'TRY',
                @secret_hash,
                'reconciliation-fixture-pin',
                null,
                null,
                null,
                null,
                'Claimed',
                0,
                @create_key,
                @claim_key,
                null,
                now() - interval '1 hour',
                now() + interval '23 hours',
                now(),
                now());

            update gift_cards.gift_cards
            set lifecycle_state = 'Suspended'
            where id = @source_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("share_id", shareId);
        command.Parameters.AddWithValue("source_id", sourceGiftCardId);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("sender_id", senderUserId);
        command.Parameters.AddWithValue("recipient_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("child_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("transaction_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("secret_hash", new string('0', 64));
        command.Parameters.AddWithValue("create_key", "broken-create-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("claim_key", "broken-claim-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
        return shareId;
    }

    private async Task SuspendOrganizationAsync(Guid organizationId)
    {
        await using var connection =
            new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(
            connection,
            transaction,
            organizationId,
            isPlatformOperator: false);
        await using var command = new NpgsqlCommand(
            """
            update organizations.organizations
            set status = 'Suspended'
            where id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", organizationId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private HttpClient FinancialClient(Guid organizationId) =>
        OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.CorporateCreditsView,
            OrganizationPermissions.GiftCardsView);

    private HttpClient IdentityClient(Guid userId)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                fixture.CreateAccessToken(userId));
        return client;
    }

    private static object OwnerLifecycleRequest() =>
        new
        {
            idempotencyKey =
                "report-owner-lifecycle-" + Guid.NewGuid().ToString("N"),
        };

    private static string ExtractQueryValue(string url, string name)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 &&
                string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        throw new InvalidOperationException($"Query value '{name}' was not present.");
    }

    private static string SummaryRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/reports/financial-summary";

    private static string HistoryRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/reports/financial-history";

    private static string ReconciliationRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/reports/reconciliation";

    private static string AuditRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/audit-records";

    private static async Task<Guid> CorrelationIdFromAsync(
        HttpResponseMessage response)
    {
        var problem = await response.Content
            .ReadFromJsonAsync<ProblemCorrelationResponse>();
        Assert.NotNull(problem);
        return problem.CorrelationId;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record AllocationResponse(Guid Id);

    private sealed record OrganizationIdResponse(Guid Id);

    private sealed record CardResponse(
        Guid Id,
        string PublicReference);

    private sealed record InvitationResponse(Guid Id);

    private sealed record DeliveryResponse(string ClaimUrl);

    private sealed record ClaimResponse(Guid OwnerUserId);

    private sealed record ProblemCorrelationResponse(Guid CorrelationId);

    private sealed record CompleteStory(
        Guid OrganizationId,
        CardResponse OwnedCard,
        Guid OwnerUserId);
}
