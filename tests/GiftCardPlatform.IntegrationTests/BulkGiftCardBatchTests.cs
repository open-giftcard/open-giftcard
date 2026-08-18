using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Distribution.Application;
using GiftCardPlatform.Modules.Distribution.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class BulkGiftCardBatchTests(PlatformApiFixture fixture)
{
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Async_batch_accepts_fifteen_hundred_durable_rows_before_processing()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var items = Enumerable.Range(1, 1_500)
            .Select(index => Item(
                $"ROW-{index:0000}",
                1m,
                RecipientContactType.Email,
                $"async-{index:0000}@example.com"))
            .ToArray();
        var response = await BatchClient(organizationId).PostAsJsonAsync(
            AsyncAcceptRoute(organizationId),
            Request(
                "ASYNC-1500",
                "async-1500-" + Guid.NewGuid().ToString("N"),
                items));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var summary = (await response.Content
            .ReadFromJsonAsync<AsyncBatchSummaryResponse>())!;
        Assert.Equal("Pending", summary.Status);
        Assert.Equal(1_500, summary.TotalItems);
        Assert.Equal(0, summary.SucceededItems);
        Assert.Equal(0, summary.FailedItems);

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            organizationId);
        Assert.Equal(
            1_500,
            await session.ScalarCountAsync(
                "select count(*) from distribution.bulk_items where batch_id = @id",
                command => command.Parameters.AddWithValue("id", summary.Id)));
        Assert.Equal(
            1_500,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.bulk_items
                where batch_id = @id
                  and state = 'Pending'
                  and recipient_contact <> ''
                  and issuance_idempotency_key <> ''
                  and distribution_idempotency_key <> ''
                """,
                command => command.Parameters.AddWithValue("id", summary.Id)));
        await DeleteAsyncBatchForTestAsync(summary.Id);
    }

    [Fact]
    public async Task Async_rows_settle_independently_page_safely_and_retry_as_immutable_child()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 100m);
        var queuedBefore = await CountQueuedNotificationsAsync();
        var client = BatchClient(organizationId);
        const string firstContact = "async-first@example.com";
        const string secondContact = "async-second@example.com";
        var accepted = await client.PostAsJsonAsync(
            AsyncAcceptRoute(organizationId),
            Request(
                "ASYNC-MIXED",
                "async-mixed-" + Guid.NewGuid().ToString("N"),
                Item("ROW-001", 100m, RecipientContactType.Email, firstContact),
                Item("ROW-002", 100m, RecipientContactType.Email, secondContact)));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var summary = (await accepted.Content
            .ReadFromJsonAsync<AsyncBatchSummaryResponse>())!;

        await ProcessOnePendingItemAsync();
        var processing = (await (await client.GetAsync(
                AsyncBatchRoute(organizationId, summary.Id)))
            .Content.ReadFromJsonAsync<AsyncBatchPageResponse>())!;
        Assert.Equal("Processing", processing.Status);
        Assert.Equal(1, processing.SucceededItems);
        Assert.Equal(0, processing.FailedItems);

        var completed = await ProcessUntilCompletedAsync(
            client,
            organizationId,
            summary.Id);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(1, completed.SucceededItems);
        Assert.Equal(1, completed.FailedItems);
        Assert.DoesNotContain(firstContact, completed.RawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondContact, completed.RawBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            completed.Items,
            item => item.Status == "Failed" &&
                item.FailureCode == "corporate_credit.balance.insufficient");
        await AssertFailedOutcomeMutationRejectedAsync(organizationId, summary.Id);

        var firstPageResponse = await client.GetAsync(
            $"{AsyncBatchRoute(organizationId, summary.Id)}?limit=1");
        firstPageResponse.EnsureSuccessStatusCode();
        var firstPage = (await firstPageResponse.Content
            .ReadFromJsonAsync<AsyncBatchPageResponse>())!;
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = (await (await client.GetAsync(
                $"{AsyncBatchRoute(organizationId, summary.Id)}?limit=1&" +
                $"cursor={Uri.EscapeDataString(firstPage.NextCursor!)}"))
            .Content.ReadFromJsonAsync<AsyncBatchPageResponse>())!;
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items[0].Position, secondPage.Items[0].Position);

        await FundAsync(organizationId, 100m);
        var retryResponse = await client.PostAsync(
            $"{AsyncBatchRoute(organizationId, summary.Id)}/retry",
            content: null);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        var retry = (await retryResponse.Content
            .ReadFromJsonAsync<AsyncBatchSummaryResponse>())!;
        Assert.Equal(summary.Id, retry.RetryOfBatchId);
        Assert.Equal(1, retry.TotalItems);
        var repeatedRetry = (await (await client.PostAsync(
                $"{AsyncBatchRoute(organizationId, summary.Id)}/retry",
                content: null)).Content
            .ReadFromJsonAsync<AsyncBatchSummaryResponse>())!;
        Assert.Equal(retry.Id, repeatedRetry.Id);

        var retryCompleted = await ProcessUntilCompletedAsync(
            client,
            organizationId,
            retry.Id);
        Assert.Equal(1, retryCompleted.SucceededItems);
        Assert.Equal(0, retryCompleted.FailedItems);

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            organizationId);
        Assert.Equal(2, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(2, await CountTransactionsAsync(
            session,
            organizationId,
            "gift_card.issuance"));
        Assert.Equal(queuedBefore + 2, await CountQueuedNotificationsAsync());
    }

    [Fact]
    public async Task Concurrent_processors_claim_distinct_pending_rows_once()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 200m);
        var queuedBefore = await CountQueuedNotificationsAsync();
        var client = BatchClient(organizationId);
        var accepted = await client.PostAsJsonAsync(
            AsyncAcceptRoute(organizationId),
            Request(
                "ASYNC-CONCURRENT",
                "async-concurrent-" + Guid.NewGuid().ToString("N"),
                Item(
                    "ROW-001",
                    100m,
                    RecipientContactType.Email,
                    "async-concurrent-1@example.com"),
                Item(
                    "ROW-002",
                    100m,
                    RecipientContactType.Email,
                    "async-concurrent-2@example.com")));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var batch = (await accepted.Content
            .ReadFromJsonAsync<AsyncBatchSummaryResponse>())!;

        var passes = await Task.WhenAll(
            ProcessOnePendingItemAsync(),
            ProcessOnePendingItemAsync());
        Assert.All(passes, result => Assert.Equal(1, result.Examined));
        Assert.Equal(0, passes.Sum(result => result.Failed));
        Assert.Equal(
            2,
            passes.Sum(result => result.Succeeded + result.Conflicted));

        _ = await ProcessUntilCompletedAsync(
            client,
            organizationId,
            batch.Id);
        var page = (await (await client.GetAsync(
                AsyncBatchRoute(organizationId, batch.Id)))
            .Content.ReadFromJsonAsync<AsyncBatchPageResponse>())!;
        Assert.Equal("Completed", page.Status);
        Assert.Equal(2, page.SucceededItems);
        Assert.Equal(0, page.FailedItems);
        Assert.Equal(2, page.Items.Select(item => item.GiftCardId).Distinct().Count());

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            organizationId);
        Assert.Equal(2, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(2, await CountTransactionsAsync(
            session,
            organizationId,
            "gift_card.issuance"));
        Assert.Equal(queuedBefore + 2, await CountQueuedNotificationsAsync());
    }

    [Fact]
    public async Task Async_batch_requires_both_permissions_and_forced_rls_hides_intent_rows()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var request = Request(
            "ASYNC-SECURITY",
            "async-security-" + Guid.NewGuid().ToString("N"),
            Item(
                "ROW-001",
                10m,
                RecipientContactType.Email,
                "async-security@example.com"));
        var issueOnly = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                organizationId,
                OrganizationPermissions.GiftCardsIssue)
            .PostAsJsonAsync(AsyncAcceptRoute(organizationId), request);
        Assert.Equal(HttpStatusCode.Forbidden, issueOnly.StatusCode);
        var distributeOnly = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                organizationId,
                OrganizationPermissions.GiftCardsDistribute)
            .PostAsJsonAsync(AsyncAcceptRoute(organizationId), request);
        Assert.Equal(HttpStatusCode.Forbidden, distributeOnly.StatusCode);

        var accepted = await BatchClient(organizationId).PostAsJsonAsync(
            AsyncAcceptRoute(organizationId),
            request);
        accepted.EnsureSuccessStatusCode();
        var batch = (await accepted.Content
            .ReadFromJsonAsync<AsyncBatchSummaryResponse>())!;
        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        await using var otherTenant = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            otherOrganizationId);
        Assert.Equal(
            0,
            await otherTenant.ScalarCountAsync(
                "select count(*) from distribution.bulk_batches where id = @id",
                command => command.Parameters.AddWithValue("id", batch.Id)));
        Assert.Equal(
            0,
            await otherTenant.ScalarCountAsync(
                "select count(*) from distribution.bulk_items where batch_id = @id",
                command => command.Parameters.AddWithValue("id", batch.Id)));
        await DeleteAsyncBatchForTestAsync(batch.Id);
    }

    [Fact]
    public async Task Successful_batch_is_atomic_queryable_and_delivered()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 500m);
        var client = BatchClient(organizationId);
        var request = Request(
            "SUCCESS-BATCH",
            "bulk-success-" + Guid.NewGuid().ToString("N"),
            Item(
                "ROW-001",
                100m,
                RecipientContactType.Email,
                "bulk-first@example.com"),
            Item(
                "ROW-002",
                75m,
                RecipientContactType.Phone,
                "+905551234567"));

        var response = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = (await response.Content
            .ReadFromJsonAsync<BulkBatchResponse>())!;

        Assert.Equal("Completed", batch.Status);
        Assert.Equal(2, batch.TotalItems);
        Assert.Equal(175m, Assert.Single(batch.CurrencyTotals).Amount);
        Assert.Equal(
            ["ROW-001", "ROW-002"],
            batch.Items.Select(item => item.ItemReference).ToArray());
        Assert.All(batch.Items, item =>
        {
            Assert.Equal("AwaitingClaim", item.GiftCardState);
            Assert.Equal("Pending", item.InvitationState);
            Assert.NotEqual(Guid.Empty, item.GiftCardId);
            Assert.NotEqual(Guid.Empty, item.InvitationId);
        });
        Assert.Equal(
            "b***@example.com",
            batch.Items[0].MaskedRecipientContact);
        Assert.Equal("+90***4567", batch.Items[1].MaskedRecipientContact);

        var query = await client.GetAsync(
            $"{BatchRoute(organizationId)}/{batch.Id}");
        query.EnsureSuccessStatusCode();
        var queried = (await query.Content
            .ReadFromJsonAsync<BulkBatchResponse>())!;
        Assert.Equal(batch.Id, queried.Id);
        Assert.Equal(
            batch.Items.Select(item => item.GiftCardId),
            queried.Items.Select(item => item.GiftCardId));

        foreach (var item in batch.Items)
        {
            var delivery = await client.GetAsync(
                $"/api/v1/development/organizations/{organizationId}/" +
                $"claim-deliveries/{item.InvitationId}");
            delivery.EnsureSuccessStatusCode();
        }

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(1, await CountAsync(
            session,
            "distribution.bulk_batches",
            organizationId));
        Assert.Equal(2, await CountAsync(
            session,
            "distribution.bulk_items",
            organizationId));
        Assert.Equal(2, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(2, await CountAsync(
            session,
            "distribution.invitations",
            organizationId));
        Assert.Equal(
            2,
            await CountTransactionsAsync(
                session,
                organizationId,
                "gift_card.issuance"));
        Assert.Equal(325m, await CorporateBalanceAsync(session, organizationId));
        Assert.Equal(
            5,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where correlation_id = @correlation_id
                  and operation in (
                      'gift_card.issued',
                      'gift_card.distributed',
                      'gift_card.bulk_distributed')
                """,
                command => command.Parameters.AddWithValue(
                    "correlation_id",
                    CorrelationIdFrom(response))));
    }

    [Fact]
    public async Task Matching_retry_returns_original_and_changed_intent_conflicts()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 200m);
        var client = BatchClient(organizationId);
        var request = Request(
            "IDEMPOTENT-BATCH",
            "bulk-idempotent-" + Guid.NewGuid().ToString("N"),
            Item(
                "ROW-001",
                100m,
                RecipientContactType.Email,
                "bulk-retry@example.com"));

        var firstTask = client.PostAsJsonAsync(BatchRoute(organizationId), request);
        var concurrentTask = client.PostAsJsonAsync(
            BatchRoute(organizationId),
            request);
        await Task.WhenAll(firstTask, concurrentTask);
        var initialResponses = new[] { await firstTask, await concurrentTask };
        var successfulResponses = initialResponses
            .Where(candidate => candidate.IsSuccessStatusCode)
            .ToArray();
        Assert.NotEmpty(successfulResponses);
        Assert.All(
            initialResponses,
            candidate => Assert.Contains(
                candidate.StatusCode,
                new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));

        var retry = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            request);
        retry.EnsureSuccessStatusCode();
        var original = (await successfulResponses[0]
            .Content.ReadFromJsonAsync<BulkBatchResponse>())!;
        foreach (var successfulResponse in successfulResponses.Skip(1))
        {
            var concurrentResult = (await successfulResponse.Content
                .ReadFromJsonAsync<BulkBatchResponse>())!;
            Assert.Equal(original.Id, concurrentResult.Id);
        }

        var retried =
            (await retry.Content.ReadFromJsonAsync<BulkBatchResponse>())!;
        Assert.Equal(original.Id, retried.Id);
        Assert.Equal(original.Items[0].GiftCardId, retried.Items[0].GiftCardId);

        var changed = request with
        {
            Items =
            [
                request.Items[0] with { Amount = 125m },
            ],
        };
        var changedResponse = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            changed);
        Assert.Equal(HttpStatusCode.Conflict, changedResponse.StatusCode);
        await AssertProblemCodeAsync(
            changedResponse,
            "bulk.idempotency_key.reused");

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(1, await CountAsync(
            session,
            "distribution.bulk_batches",
            organizationId));
        Assert.Equal(1, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(
            1,
            await CountTransactionsAsync(
                session,
                organizationId,
                "gift_card.issuance"));
    }

    [Fact]
    public async Task Invalid_item_and_duplicate_reference_leave_no_work()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 500m);
        var client = BatchClient(organizationId);
        var invalidContact = "private-invalid-contact";
        var invalid = Request(
            "INVALID-BATCH",
            "bulk-invalid-" + Guid.NewGuid().ToString("N"),
            Item(
                "ROW-001",
                100m,
                RecipientContactType.Email,
                "valid@example.com"),
            Item(
                "ROW-002",
                100m,
                RecipientContactType.Email,
                invalidContact));

        var invalidResponse = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        var invalidProblem = await ReadProblemAsync(invalidResponse);
        Assert.Equal("bulk.item.invalid", invalidProblem.Code);
        Assert.Equal(1, invalidProblem.ItemIndex);
        Assert.Equal("ROW-002", invalidProblem.ItemReference);
        Assert.Equal("distribution.email.invalid", invalidProblem.CauseCode);
        Assert.DoesNotContain(
            invalidContact,
            await invalidResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var duplicate = Request(
            "DUPLICATE-BATCH",
            "bulk-duplicate-" + Guid.NewGuid().ToString("N"),
            Item(
                "ROW-001",
                100m,
                RecipientContactType.Email,
                "first@example.com"),
            Item(
                "ROW-001",
                100m,
                RecipientContactType.Email,
                "second@example.com"));
        var duplicateResponse = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
        var duplicateProblem = await ReadProblemAsync(duplicateResponse);
        Assert.Equal("bulk.item_reference.duplicate", duplicateProblem.CauseCode);

        await AssertNoBulkWorkAsync(organizationId);
    }

    [Fact]
    public async Task Insufficient_aggregate_funding_rolls_back_every_item()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 150m);
        var queuedBefore = await CountQueuedNotificationsAsync();
        var response = await BatchClient(organizationId).PostAsJsonAsync(
            BatchRoute(organizationId),
            Request(
                "ROLLBACK-BATCH",
                "bulk-rollback-" + Guid.NewGuid().ToString("N"),
                Item(
                    "ROW-001",
                    100m,
                    RecipientContactType.Email,
                    "rollback-first@example.com"),
                Item(
                    "ROW-002",
                    100m,
                    RecipientContactType.Phone,
                    "+905551234567")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("bulk.item.conflict", problem.Code);
        Assert.Equal(1, problem.ItemIndex);
        Assert.Equal("corporate_credit.balance.insufficient", problem.CauseCode);

        await AssertNoBulkWorkAsync(organizationId);
        // The whole point of enqueueing inside the transaction: a batch that
        // rolls back leaves no activation message behind for a card nobody got.
        Assert.Equal(queuedBefore, await CountQueuedNotificationsAsync());
        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(150m, await CorporateBalanceAsync(session, organizationId));
    }

    [Fact]
    public async Task One_hundred_items_succeed_and_one_hundred_one_are_rejected()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 100m);
        var client = BatchClient(organizationId);
        var hundred = Enumerable
            .Range(1, 100)
            .Select(index => Item(
                $"ROW-{index:000}",
                1m,
                RecipientContactType.Email,
                $"bulk-{index:000}@example.com"))
            .ToArray();

        var success = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            Request(
                "MAXIMUM-BATCH",
                "bulk-maximum-" + Guid.NewGuid().ToString("N"),
                hundred));
        success.EnsureSuccessStatusCode();
        var result =
            (await success.Content.ReadFromJsonAsync<BulkBatchResponse>())!;
        Assert.Equal(100, result.TotalItems);
        Assert.Equal(100, result.Items.Count);
        Assert.Equal(100m, Assert.Single(result.CurrencyTotals).Amount);

        var tooMany = await client.PostAsJsonAsync(
            BatchRoute(organizationId),
            Request(
                "TOO-MANY-BATCH",
                "bulk-too-many-" + Guid.NewGuid().ToString("N"),
                [.. hundred, Item(
                    "ROW-101",
                    1m,
                    RecipientContactType.Email,
                    "bulk-101@example.com")]));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        await AssertProblemCodeAsync(tooMany, "bulk.items.invalid_count");

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(100, await CountAsync(
            session,
            "distribution.bulk_items",
            organizationId));
        Assert.Equal(100, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(0m, await CorporateBalanceAsync(session, organizationId));
    }

    [Fact]
    public async Task Concurrent_batches_cannot_overspend_corporate_credit()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 150m);
        var client = BatchClient(organizationId);
        var firstTask = client.PostAsJsonAsync(
            BatchRoute(organizationId),
            Request(
                "CONCURRENT-A",
                "bulk-concurrent-a-" + Guid.NewGuid().ToString("N"),
                Item(
                    "ROW-A",
                    100m,
                    RecipientContactType.Email,
                    "concurrent-a@example.com")));
        var secondTask = client.PostAsJsonAsync(
            BatchRoute(organizationId),
            Request(
                "CONCURRENT-B",
                "bulk-concurrent-b-" + Guid.NewGuid().ToString("N"),
                Item(
                    "ROW-B",
                    100m,
                    RecipientContactType.Email,
                    "concurrent-b@example.com")));

        await Task.WhenAll(firstTask, secondTask);
        var responses = new[] { await firstTask, await secondTask };
        Assert.Single(responses, response => response.IsSuccessStatusCode);
        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(1, await CountAsync(
            session,
            "distribution.bulk_batches",
            organizationId));
        Assert.Equal(1, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(50m, await CorporateBalanceAsync(session, organizationId));
    }

    [Fact]
    public async Task Permissions_tenant_isolation_and_immutable_history_are_enforced()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 200m);
        var request = Request(
            "SECURITY-BATCH",
            "bulk-security-" + Guid.NewGuid().ToString("N"),
            Item(
                "ROW-001",
                100m,
                RecipientContactType.Email,
                "security@example.com"));

        var issueOnly = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                organizationId,
                OrganizationPermissions.GiftCardsIssue)
            .PostAsJsonAsync(BatchRoute(organizationId), request);
        Assert.Equal(HttpStatusCode.Forbidden, issueOnly.StatusCode);

        var distributeOnly = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                organizationId,
                OrganizationPermissions.GiftCardsDistribute)
            .PostAsJsonAsync(BatchRoute(organizationId), request);
        Assert.Equal(HttpStatusCode.Forbidden, distributeOnly.StatusCode);

        var created = await BatchClient(organizationId).PostAsJsonAsync(
            BatchRoute(organizationId),
            request);
        created.EnsureSuccessStatusCode();
        var batch =
            (await created.Content.ReadFromJsonAsync<BulkBatchResponse>())!;

        var viewOnly = OrganizationMember(
            fixture,
            Guid.CreateVersion7(),
            organizationId,
            OrganizationPermissions.GiftCardsView);
        (await viewOnly.GetAsync(
                $"{BatchRoute(organizationId)}/{batch.Id}"))
            .EnsureSuccessStatusCode();

        var noView = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                organizationId,
                OrganizationPermissions.GiftCardsDistribute)
            .GetAsync($"{BatchRoute(organizationId)}/{batch.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, noView.StatusCode);

        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var crossTenant = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                otherOrganizationId,
                OrganizationPermissions.GiftCardsView)
            .GetAsync($"{BatchRoute(otherOrganizationId)}/{batch.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        await using (var otherTenant =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                otherOrganizationId))
        {
            Assert.Equal(
                0,
                await otherTenant.ScalarCountAsync(
                    """
                    select count(*)
                    from distribution.bulk_batches
                    where id = @id
                    """,
                    command => command.Parameters.AddWithValue("id", batch.Id)));
            Assert.Equal(
                0,
                await otherTenant.ScalarCountAsync(
                    """
                    select count(*)
                    from distribution.bulk_items
                    where batch_id = @id
                    """,
                    command => command.Parameters.AddWithValue("id", batch.Id)));
        }

        await AssertHistoryMutationRejectedAsync(
            organizationId,
            batch.Id,
            batch.Items[0].GiftCardId);
    }

    private async Task AssertNoBulkWorkAsync(Guid organizationId)
    {
        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                organizationId);
        Assert.Equal(0, await CountAsync(
            session,
            "distribution.bulk_batches",
            organizationId));
        Assert.Equal(0, await CountAsync(
            session,
            "distribution.bulk_items",
            organizationId));
        Assert.Equal(0, await CountAsync(
            session,
            "gift_cards.gift_cards",
            organizationId));
        Assert.Equal(0, await CountAsync(
            session,
            "distribution.invitations",
            organizationId));
        Assert.Equal(
            0,
            await CountTransactionsAsync(
                session,
                organizationId,
                "gift_card.issuance"));
    }

    private async Task AssertHistoryMutationRejectedAsync(
        Guid organizationId,
        Guid batchId,
        Guid giftCardId)
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

        await using (var updateBatch = new NpgsqlCommand(
            """
            update distribution.bulk_batches
            set batch_reference = 'tampered'
            where id = @id
            """,
            connection,
            transaction))
        {
            updateBatch.Parameters.AddWithValue("id", batchId);
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => updateBatch.ExecuteNonQueryAsync());
            Assert.Equal("55000", exception.SqlState);
        }

        await transaction.RollbackAsync();
        await using var deleteTransaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(
            connection,
            deleteTransaction,
            organizationId,
            isPlatformOperator: false);
        await using var deleteItem = new NpgsqlCommand(
            """
            delete from distribution.bulk_items
            where gift_card_id = @id
            """,
            connection,
            deleteTransaction);
        deleteItem.Parameters.AddWithValue("id", giftCardId);
        var deletion = await Assert.ThrowsAsync<PostgresException>(
            () => deleteItem.ExecuteNonQueryAsync());
        Assert.Equal("55000", deletion.SqlState);
        await deleteTransaction.RollbackAsync();
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
                    businessReference =
                        "BULK-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey =
                        "bulk-fund-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private HttpClient BatchClient(Guid organizationId) =>
        OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsIssue,
            OrganizationPermissions.GiftCardsDistribute,
            OrganizationPermissions.GiftCardsView);

    private static BulkBatchRequest Request(
        string batchReference,
        string idempotencyKey,
        params BulkItemRequest[] items) =>
        new(batchReference, idempotencyKey, items);

    private static BulkItemRequest Item(
        string itemReference,
        decimal amount,
        RecipientContactType contactType,
        string recipientContact) =>
        new(
            itemReference,
            amount,
            "TRY",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            false,
            false,
            contactType,
            recipientContact);

    private static string BatchRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/gift-card-batches";

    private static string AsyncAcceptRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/bulk-batches/async";

    private static string AsyncBatchRoute(Guid organizationId, Guid batchId) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/bulk-batches/{batchId}";

    private async Task<ProcessedBatch> ProcessUntilCompletedAsync(
        HttpClient client,
        Guid organizationId,
        Guid batchId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await ProcessOnePendingItemAsync();
            var response = await client.GetAsync(AsyncBatchRoute(organizationId, batchId));
            response.EnsureSuccessStatusCode();
            var rawBody = await response.Content.ReadAsStringAsync();
            var page = JsonSerializer.Deserialize<AsyncBatchPageResponse>(
                rawBody,
                WebJsonOptions)!;
            if (page.Status == "Completed")
            {
                return new ProcessedBatch(
                    page.Status,
                    page.SucceededItems,
                    page.FailedItems,
                    page.Items,
                    rawBody);
            }
        }

        throw new Xunit.Sdk.XunitException("The asynchronous batch did not complete.");
    }

    private async Task<BulkGiftCardBatchProcessingResult> ProcessOnePendingItemAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
        context.SetCorrelationId(Guid.CreateVersion7());
        context.SetSystem(SystemActorIds.BulkGiftCardBatch, []);
        return await scope.ServiceProvider
            .GetRequiredService<IBulkGiftCardBatchProcessor>()
            .ProcessPendingAsync(1, CancellationToken.None);
    }

    private async Task AssertFailedOutcomeMutationRejectedAsync(
        Guid organizationId,
        Guid batchId)
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
            update distribution.bulk_items
            set failure_code = 'tampered'
            where batch_id = @batch_id and state = 'Failed'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("batch_id", batchId);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", exception.SqlState);
        await transaction.RollbackAsync();
    }

    private async Task DeleteAsyncBatchForTestAsync(Guid batchId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            alter table distribution.bulk_items disable trigger user;
            alter table distribution.bulk_batches disable trigger user;
            delete from distribution.bulk_items where batch_id = @id;
            delete from distribution.bulk_batches where id = @id;
            alter table distribution.bulk_batches enable trigger user;
            alter table distribution.bulk_items enable trigger user;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", batchId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static Guid CorrelationIdFrom(HttpResponseMessage response) =>
        Guid.Parse(
            Assert.Single(
                response.Headers.GetValues("X-Correlation-Id")));

    private static async Task AssertProblemCodeAsync(
        HttpResponseMessage response,
        string expected)
    {
        var problem = await ReadProblemAsync(response);
        Assert.Equal(expected, problem.Code);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(
        HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return new ProblemResponse(
            root.GetProperty("code").GetString()!,
            root.TryGetProperty("itemIndex", out var index)
                ? index.GetInt32()
                : null,
            root.TryGetProperty("itemReference", out var itemReference) &&
            itemReference.ValueKind != JsonValueKind.Null
                ? itemReference.GetString()
                : null,
            root.TryGetProperty("causeCode", out var causeCode)
                ? causeCode.GetString()
                : null);
    }

    private static async Task<int> CountAsync(
        ScopedSqlSession session,
        string table,
        Guid organizationId)
    {
        await using var command = session.Command(
            $"select count(*) from {table} where funding_organization_id = @id");
        command.Parameters.AddWithValue("id", organizationId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountTransactionsAsync(
        ScopedSqlSession session,
        Guid organizationId,
        string operationType)
    {
        await using var command = session.Command(
            """
            select count(*)
            from ledger.transactions
            where organization_id = @organization_id
              and operation_type = @operation_type
            """);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("operation_type", operationType);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<decimal> CorporateBalanceAsync(
        ScopedSqlSession session,
        Guid organizationId)
    {
        await using var command = session.Command(
            """
            select coalesce(sum(
                case entry.direction
                    when 'Credit' then entry.amount
                    else -entry.amount
                end), 0)
            from ledger.entries entry
            join ledger.accounts account on account.id = entry.account_id
            where account.organization_id = @organization_id
              and account.type = 'OrganizationCorporateCredit'
            """);
        command.Parameters.AddWithValue("organization_id", organizationId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private sealed record BulkBatchRequest(
        string BatchReference,
        string IdempotencyKey,
        IReadOnlyList<BulkItemRequest> Items);

    private sealed record BulkItemRequest(
        string ItemReference,
        decimal Amount,
        string Currency,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset ExpiresAtUtc,
        bool IsTransferable,
        bool IsDivisible,
        RecipientContactType ContactType,
        string RecipientContact);

    private sealed record BulkBatchResponse(
        Guid Id,
        string Status,
        int TotalItems,
        IReadOnlyList<CurrencyTotalResponse> CurrencyTotals,
        IReadOnlyList<BulkItemResponse> Items);

    private sealed record CurrencyTotalResponse(
        string Currency,
        decimal Amount);

    private sealed record BulkItemResponse(
        int Position,
        string ItemReference,
        Guid GiftCardId,
        string GiftCardPublicReference,
        Guid InvitationId,
        string MaskedRecipientContact,
        decimal Amount,
        string Currency,
        string GiftCardState,
        string InvitationState);

    private sealed record AsyncBatchSummaryResponse(
        Guid Id,
        string Status,
        int TotalItems,
        int SucceededItems,
        int FailedItems,
        Guid? RetryOfBatchId);

    private sealed record AsyncBatchPageResponse(
        Guid Id,
        string Status,
        int TotalItems,
        int SucceededItems,
        int FailedItems,
        string? NextCursor,
        IReadOnlyList<AsyncBatchItemResponse> Items);

    private sealed record AsyncBatchItemResponse(
        int Position,
        string ItemReference,
        string Status,
        Guid? GiftCardId,
        Guid? InvitationId,
        string MaskedRecipientContact,
        string? FailureCode);

    private sealed record ProcessedBatch(
        string Status,
        int SucceededItems,
        int FailedItems,
        IReadOnlyList<AsyncBatchItemResponse> Items,
        string RawBody);

    private sealed record ProblemResponse(
        string Code,
        int? ItemIndex,
        string? ItemReference,
        string? CauseCode);

    private async Task<long> CountQueuedNotificationsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var context = new NpgsqlCommand(
            "select set_config('app.is_platform_operator', 'true', false)",
            connection))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "select count(*) from notifications.outbox_messages",
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
