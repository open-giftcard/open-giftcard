using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class GiftCardLifecycleTests(PlatformApiFixture fixture)
{
    private const string Password = "recipient lifecycle passphrase";

    [Fact]
    public async Task Organization_suspend_reactivate_history_and_idempotency_are_atomic()
    {
        var setup = await PrepareCardAsync();
        var client = LifecycleClient(setup.OrganizationId);
        var suspendRequest = new
        {
            reason = "Card reported temporarily missing.",
            idempotencyKey = "suspend-" + Guid.NewGuid().ToString("N"),
        };

        var first = await client.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "suspend"),
            suspendRequest);
        var retry = await client.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "suspend"),
            suspendRequest);

        first.EnsureSuccessStatusCode();
        retry.EnsureSuccessStatusCode();
        var firstResult =
            (await first.Content.ReadFromJsonAsync<LifecycleOperationResponse>())!;
        var retryResult =
            (await retry.Content.ReadFromJsonAsync<LifecycleOperationResponse>())!;
        Assert.Equal(firstResult, retryResult);
        Assert.Equal("Suspend", firstResult.Event.Action);
        Assert.Equal("Active", firstResult.Event.PreviousState);
        Assert.Equal("Suspended", firstResult.Event.NewState);
        Assert.Equal("OrganizationMember", firstResult.Event.ActorType);
        Assert.NotEqual(Guid.Empty, firstResult.Event.CorrelationId);
        Assert.Null(firstResult.Event.LedgerTransactionId);
        Assert.Null(firstResult.Event.ReturnedAmount);

        var changedIntent = await client.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "suspend"),
            new
            {
                reason = "A different reason for the same key.",
                suspendRequest.idempotencyKey,
            });
        Assert.Equal(HttpStatusCode.Conflict, changedIntent.StatusCode);

        var reactivate = await client.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "reactivate"),
            AdminRequest("Card recovered."));
        reactivate.EnsureSuccessStatusCode();

        var historyResponse = await client.GetAsync(
            OrganizationHistoryRoute(setup.OrganizationId, setup.Card.Id));
        historyResponse.EnsureSuccessStatusCode();
        var history =
            (await historyResponse.Content.ReadFromJsonAsync<LifecycleHistoryResponse>())!;
        Assert.Equal("Active", history.GiftCard.LifecycleState);
        Assert.Collection(
            history.Events,
            item =>
            {
                Assert.Equal("Reactivate", item.Action);
                Assert.Equal("Suspended", item.PreviousState);
                Assert.Equal("Active", item.NewState);
            },
            item =>
            {
                Assert.Equal("Suspend", item.Action);
                Assert.Equal("Active", item.PreviousState);
                Assert.Equal("Suspended", item.NewState);
            });

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records audit
                join gift_cards.lifecycle_events lifecycle
                  on lifecycle.gift_card_id = @card_id
                 and lifecycle.correlation_id = audit.correlation_id
                where entity_id = @id
                  and operation in (
                      'gift_card.suspended',
                      'gift_card.reactivated')
                """,
                command =>
                {
                    command.Parameters.AddWithValue("card_id", setup.Card.Id);
                    command.Parameters.AddWithValue(
                        "id",
                        setup.Card.Id.ToString());
                }));
        Assert.Equal(100m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
        Assert.Equal(400m, await CorporateBalanceAsync(session, setup.OrganizationId));

        var wrongPermission = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                setup.OrganizationId,
                OrganizationPermissions.GiftCardsView)
            .PostAsJsonAsync(
                OrganizationLifecycleRoute(
                    setup.OrganizationId,
                    setup.Card.Id,
                    "suspend"),
                AdminRequest("Must be denied."));
        Assert.Equal(HttpStatusCode.Forbidden, wrongPermission.StatusCode);

        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var crossTenant = await LifecycleClient(otherOrganizationId).PostAsJsonAsync(
            OrganizationLifecycleRoute(
                otherOrganizationId,
                setup.Card.Id,
                "suspend"),
            AdminRequest("Cross-tenant attempt."));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
    }

    [Fact]
    public async Task Organization_cancellation_returns_value_once_and_terminal_data_is_immutable()
    {
        var setup = await PrepareCardAsync();
        var client = LifecycleClient(setup.OrganizationId);
        var request = AdminRequest("Recipient award withdrawn.");
        var route = OrganizationLifecycleRoute(
            setup.OrganizationId,
            setup.Card.Id,
            "cancel");

        var first = await client.PostAsJsonAsync(route, request);
        var retry = await client.PostAsJsonAsync(route, request);
        first.EnsureSuccessStatusCode();
        retry.EnsureSuccessStatusCode();
        var firstResult =
            (await first.Content.ReadFromJsonAsync<LifecycleOperationResponse>())!;
        var retryResult =
            (await retry.Content.ReadFromJsonAsync<LifecycleOperationResponse>())!;
        Assert.Equal(firstResult, retryResult);
        Assert.Equal("Cancel", firstResult.Event.Action);
        Assert.Equal("Cancelled", firstResult.Event.NewState);
        Assert.Equal(100m, firstResult.Event.ReturnedAmount);
        Assert.Equal("TRY", firstResult.Event.Currency);
        Assert.NotNull(firstResult.Event.LedgerTransactionId);

        var terminalRetryWithNewKey = await client.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "reactivate"),
            AdminRequest("Invalid terminal transition."));
        Assert.Equal(HttpStatusCode.Conflict, terminalRetryWithNewKey.StatusCode);

        await using (var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId))
        {
            Assert.Equal(0m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
            Assert.Equal(
                500m,
                await CorporateBalanceAsync(session, setup.OrganizationId));
            Assert.Equal(
                1,
                await session.ScalarCountAsync(
                    """
                    select count(*)
                    from ledger.transactions
                    where organization_id = @organization_id
                      and operation_type = 'gift_card.cancellation_return'
                      and reverses_transaction_id = @issuance_id
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue(
                            "organization_id",
                            setup.OrganizationId);
                        command.Parameters.AddWithValue(
                            "issuance_id",
                            setup.Card.IssuanceLedgerTransactionId);
                    }));
            Assert.Equal(
                1,
                await session.ScalarCountAsync(
                    """
                    select count(*)
                    from gift_cards.lifecycle_events
                    where gift_card_id = @id
                      and action = 'Cancel'
                    """,
                    command => command.Parameters.AddWithValue("id", setup.Card.Id)));
        }

        await AssertTerminalCardMutationRejectedAsync(setup);
        await AssertLifecycleEventMutationRejectedAsync(setup);

        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        await using var otherTenant =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                otherOrganizationId);
        Assert.Equal(
            0,
            await otherTenant.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));

        await using var platform = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await platform.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
    }

    [Fact]
    public async Task Zero_balance_cancellation_records_history_without_zero_value_posting()
    {
        var setup = await PrepareCardAsync();
        await SimulateFullyConsumedCardAsync(setup);

        var response = await LifecycleClient(setup.OrganizationId)
            .PostAsJsonAsync(
                OrganizationLifecycleRoute(
                    setup.OrganizationId,
                    setup.Card.Id,
                    "cancel"),
                AdminRequest("Close fully consumed card."));
        response.EnsureSuccessStatusCode();
        var result =
            (await response.Content.ReadFromJsonAsync<LifecycleOperationResponse>())!;
        Assert.Equal(0m, result.Event.ReturnedAmount);
        Assert.Equal("TRY", result.Event.Currency);
        Assert.Null(result.Event.LedgerTransactionId);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        Assert.Equal(0m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where organization_id = @organization_id
                  and operation_type = 'gift_card.cancellation_return'
                """,
                command => command.Parameters.AddWithValue(
                    "organization_id",
                    setup.OrganizationId)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                  and action = 'Cancel'
                  and returned_amount = 0
                  and currency = 'TRY'
                  and ledger_transaction_id is null
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
    }

    [Fact]
    public async Task Cancelling_an_awaiting_claim_card_closes_its_invitation()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(setup);
        var claimToken = await GetClaimTokenAsync(
            setup.OrganizationId,
            invitation.Id);

        var cancellation = await LifecycleClient(setup.OrganizationId)
            .PostAsJsonAsync(
                OrganizationLifecycleRoute(
                    setup.OrganizationId,
                    setup.Card.Id,
                    "cancel"),
                AdminRequest("Distribution recipient changed."));
        cancellation.EnsureSuccessStatusCode();

        var claim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken,
                password = Password,
                idempotencyKey = "claim-" + Guid.NewGuid().ToString("N"),
            });
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.invitations
                where id = @id
                  and state = 'Cancelled'
                """,
                command => command.Parameters.AddWithValue("id", invitation.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.events
                where invitation_id = @id
                  and event_type = 'CardCancelled'
                """,
                command => command.Parameters.AddWithValue("id", invitation.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.gift_cards
                where id = @id
                  and ownership_state = 'AwaitingClaim'
                  and lifecycle_state = 'Cancelled'
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
    }

    [Fact]
    public async Task Awaiting_claim_suspension_pauses_activation_until_reactivated()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(setup);
        var claimToken = await GetClaimTokenAsync(
            setup.OrganizationId,
            invitation.Id);
        var lifecycle = LifecycleClient(setup.OrganizationId);

        var suspend = await lifecycle.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "suspend"),
            AdminRequest("Pause recipient activation."));
        suspend.EnsureSuccessStatusCode();

        var claimRequest = new
        {
            claimToken,
            password = Password,
            idempotencyKey = "claim-" + Guid.NewGuid().ToString("N"),
        };
        var pausedClaim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            claimRequest);
        Assert.Equal(HttpStatusCode.Conflict, pausedClaim.StatusCode);

        await using (var pausedSession =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId))
        {
            Assert.Equal(
                1,
                await pausedSession.ScalarCountAsync(
                    """
                    select count(*)
                    from distribution.invitations
                    where id = @id
                      and state = 'Pending'
                    """,
                    command => command.Parameters.AddWithValue(
                        "id",
                        invitation.Id)));
        }

        var reactivate = await lifecycle.PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "reactivate"),
            AdminRequest("Resume recipient activation."));
        reactivate.EnsureSuccessStatusCode();
        var activation = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            claimRequest);
        activation.EnsureSuccessStatusCode();

        await using var completedSession =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        Assert.Equal(
            1,
            await completedSession.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.gift_cards card
                join distribution.invitations invitation
                  on invitation.id = card.distribution_invitation_id
                where card.id = @card_id
                  and card.ownership_state = 'IdentityOwned'
                  and card.lifecycle_state = 'Active'
                  and invitation.state = 'Claimed'
                """,
                command => command.Parameters.AddWithValue(
                    "card_id",
                    setup.Card.Id)));
    }

    [Fact]
    public async Task Claimed_card_is_owner_managed_but_platform_can_emergency_cancel()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(setup);
        var claimToken = await GetClaimTokenAsync(
            setup.OrganizationId,
            invitation.Id);
        var claimResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken,
                password = Password,
                idempotencyKey = "claim-" + Guid.NewGuid().ToString("N"),
            });
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>())!;

        var companyCancellation = await LifecycleClient(setup.OrganizationId)
            .PostAsJsonAsync(
                OrganizationLifecycleRoute(
                    setup.OrganizationId,
                    setup.Card.Id,
                    "cancel"),
                AdminRequest("Company post-claim attempt."));
        Assert.Equal(HttpStatusCode.Forbidden, companyCancellation.StatusCode);

        var owner = IdentityClient(claim.OwnerUserId);
        var suspend = await owner.PostAsJsonAsync(
            OwnerLifecycleRoute(setup.Card.Id, "suspend"),
            OwnerRequest());
        suspend.EnsureSuccessStatusCode();
        var reactivate = await owner.PostAsJsonAsync(
            OwnerLifecycleRoute(setup.Card.Id, "reactivate"),
            OwnerRequest());
        reactivate.EnsureSuccessStatusCode();

        var unrelatedOwner = await IdentityClient(Guid.CreateVersion7())
            .PostAsJsonAsync(
                OwnerLifecycleRoute(setup.Card.Id, "suspend"),
            OwnerRequest());
        Assert.Equal(HttpStatusCode.NotFound, unrelatedOwner.StatusCode);

        var wrongPlatformPermission = await PlatformOperator(
                fixture,
                PlatformPermissions.GiftCardsView)
            .PostAsJsonAsync(
                PlatformLifecycleRoute(setup.Card.Id, "cancel"),
                AdminRequest("Must be denied."));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            wrongPlatformPermission.StatusCode);

        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.GiftCardsManageLifecycle,
            PlatformPermissions.GiftCardsView);
        var emergencyCancellation = await platform.PostAsJsonAsync(
            PlatformLifecycleRoute(setup.Card.Id, "cancel"),
            AdminRequest("Platform emergency fraud response."));
        emergencyCancellation.EnsureSuccessStatusCode();
        var cancellation =
            (await emergencyCancellation.Content
                .ReadFromJsonAsync<LifecycleOperationResponse>())!;
        Assert.Equal("PlatformOperator", cancellation.Event.ActorType);
        Assert.Equal(100m, cancellation.Event.ReturnedAmount);

        var ownerHistory = await owner.GetAsync(OwnerHistoryRoute(setup.Card.Id));
        ownerHistory.EnsureSuccessStatusCode();
        var history =
            (await ownerHistory.Content.ReadFromJsonAsync<LifecycleHistoryResponse>())!;
        Assert.Equal("Cancelled", history.GiftCard.LifecycleState);
        Assert.Equal(
            ["Cancel", "Reactivate", "Suspend"],
            history.Events.Select(item => item.Action).ToArray());

        await using var ownerSession =
            await ScopedSqlSession.OpenAsIdentityAsync(
                fixture,
                claim.OwnerUserId);
        Assert.Equal(
            3,
            await ownerSession.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
    }

    [Fact]
    public async Task Due_card_is_expired_by_the_trusted_processor_once()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var setup = await PrepareCardAsync(
            validFromUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc: expiresAt);
        var delay = expiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var executionContext =
            scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
        executionContext.SetSystem(
            SystemActorIds.GiftCardExpiration,
            [PlatformPermissions.GiftCardsManageLifecycle]);
        var processor =
            scope.ServiceProvider.GetRequiredService<IGiftCardExpirationProcessor>();

        var first = await processor.ProcessDueAsync(10, CancellationToken.None);
        var retry = await processor.ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(1, first.Examined);
        Assert.Equal(1, first.Expired);
        Assert.Equal(0, first.Conflicted);
        Assert.Equal(0, retry.Examined);
        Assert.Equal(0, retry.Expired);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                  and action = 'Expire'
                  and actor_type = 'System'
                  and returned_amount = 100
                  and ledger_transaction_id is not null
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where organization_id = @organization_id
                  and operation_type = 'gift_card.expiration_return'
                """,
                command => command.Parameters.AddWithValue(
                    "organization_id",
                    setup.OrganizationId)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where entity_id = @id
                  and operation = 'gift_card.expired'
                  and actor_type = 'System'
                """,
                command => command.Parameters.AddWithValue(
                    "id",
                    setup.Card.Id.ToString())));
    }

    [Fact]
    public async Task Concurrent_claim_and_company_cancel_leave_one_coherent_outcome()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(setup);
        var claimToken = await GetClaimTokenAsync(
            setup.OrganizationId,
            invitation.Id);

        var claimTask = fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken,
                password = Password,
                idempotencyKey = "claim-" + Guid.NewGuid().ToString("N"),
            });
        var cancelTask = LifecycleClient(setup.OrganizationId).PostAsJsonAsync(
            OrganizationLifecycleRoute(
                setup.OrganizationId,
                setup.Card.Id,
                "cancel"),
            AdminRequest("Concurrent cancellation test."));
        await Task.WhenAll(claimTask, cancelTask);
        var claimResponse = await claimTask;
        var cancelResponse = await cancelTask;

        var claimSucceeded = claimResponse.IsSuccessStatusCode;
        var cancelSucceeded = cancelResponse.IsSuccessStatusCode;
        Assert.NotEqual(claimSucceeded, cancelSucceeded);
        Assert.Contains(
            claimResponse.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Contains(
            cancelResponse.StatusCode,
            new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.Conflict,
                HttpStatusCode.Forbidden,
            });

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        var outcome = await ReadCardAndInvitationStateAsync(
            session,
            setup.Card.Id,
            invitation.Id);

        if (claimSucceeded)
        {
            Assert.Equal(("IdentityOwned", "Active", "Claimed"), outcome);
            Assert.Equal(100m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
            Assert.Equal(400m, await CorporateBalanceAsync(session, setup.OrganizationId));
        }
        else
        {
            Assert.Equal(("AwaitingClaim", "Cancelled", "Cancelled"), outcome);
            Assert.Equal(0m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
            Assert.Equal(500m, await CorporateBalanceAsync(session, setup.OrganizationId));
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_cancellations_return_value_once()
    {
        var setup = await PrepareCardAsync();
        var client = LifecycleClient(setup.OrganizationId);
        var route = OrganizationLifecycleRoute(
            setup.OrganizationId,
            setup.Card.Id,
            "cancel");

        var firstTask = client.PostAsJsonAsync(
            route,
            AdminRequest("First concurrent cancellation."));
        var secondTask = client.PostAsJsonAsync(
            route,
            AdminRequest("Second concurrent cancellation."));
        await Task.WhenAll(firstTask, secondTask);
        var responses = new[] { await firstTask, await secondTask };

        Assert.Single(responses, response => response.IsSuccessStatusCode);
        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        Assert.Equal(0m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
        Assert.Equal(500m, await CorporateBalanceAsync(session, setup.OrganizationId));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                  and action = 'Cancel'
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where organization_id = @organization_id
                  and operation_type = 'gift_card.cancellation_return'
                  and reverses_transaction_id = @issuance_id
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "organization_id",
                        setup.OrganizationId);
                    command.Parameters.AddWithValue(
                        "issuance_id",
                        setup.Card.IssuanceLedgerTransactionId);
                }));
    }

    [Fact]
    public async Task Expiration_and_claim_race_closes_activation_and_returns_once()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var setup = await PrepareCardAsync(
            validFromUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc: expiresAt);
        var invitation = await DistributeAsync(setup);
        var claimToken = await GetClaimTokenAsync(
            setup.OrganizationId,
            invitation.Id);
        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.GiftCardsManageLifecycle);

        var delay = expiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        var claimTask = fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken,
                password = Password,
                idempotencyKey = "claim-" + Guid.NewGuid().ToString("N"),
            });
        var expirationTask = platform.PostAsJsonAsync(
            PlatformLifecycleRoute(setup.Card.Id, "expire"),
            AdminRequest("Explicit expiry race verification."));
        await Task.WhenAll(claimTask, expirationTask);
        var claimResponse = await claimTask;
        var expirationResponse = await expirationTask;

        Assert.Equal(HttpStatusCode.Conflict, claimResponse.StatusCode);
        expirationResponse.EnsureSuccessStatusCode();

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            ("AwaitingClaim", "Expired", "Expired"),
            await ReadCardAndInvitationStateAsync(
                session,
                setup.Card.Id,
                invitation.Id));
        Assert.Equal(0m, await CardBalanceAsync(session, setup.Card.LedgerAccountId));
        Assert.Equal(500m, await CorporateBalanceAsync(session, setup.OrganizationId));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from gift_cards.lifecycle_events
                where gift_card_id = @id
                  and action = 'Expire'
                """,
                command => command.Parameters.AddWithValue("id", setup.Card.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.transactions
                where organization_id = @organization_id
                  and operation_type = 'gift_card.expiration_return'
                  and reverses_transaction_id = @issuance_id
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "organization_id",
                        setup.OrganizationId);
                    command.Parameters.AddWithValue(
                        "issuance_id",
                        setup.Card.IssuanceLedgerTransactionId);
                }));
    }

    private async Task<CardSetup> PrepareCardAsync(
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await FundAsync(organizationId, 500m);
        var response = await OrganizationMember(
                fixture,
                organizationId,
                OrganizationPermissions.GiftCardsIssue,
                OrganizationPermissions.GiftCardsView)
            .PostAsJsonAsync(
                $"/api/v1/organizations/{organizationId}/gift-cards/",
                new
                {
                    amount = 100m,
                    currency = "TRY",
                    validFromUtc,
                    expiresAtUtc =
                        expiresAtUtc ?? DateTimeOffset.UtcNow.AddYears(1),
                    businessReference =
                        "LIFECYCLE-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey =
                        "gift-card-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return new CardSetup(
            organizationId,
            (await response.Content.ReadFromJsonAsync<CardResponse>())!);
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
                        "LIFECYCLE-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey =
                        "allocation-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private async Task SimulateFullyConsumedCardAsync(CardSetup setup)
    {
        await using var connection =
            new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(
            connection,
            transaction,
            setup.OrganizationId,
            isPlatformOperator: false);

        Guid corporateAccountId;
        await using (var account = new NpgsqlCommand(
            """
            select id
            from ledger.accounts
            where organization_id = @organization_id
              and type = 'OrganizationCorporateCredit'
              and currency = 'TRY'
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "organization_id",
                setup.OrganizationId);
            corporateAccountId = (Guid)(await account.ExecuteScalarAsync())!;
        }

        var transactionId = Guid.CreateVersion7();
        await using var posting = new NpgsqlCommand(
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
                'test.gift_card_consumption',
                'TEST-FULL-CONSUMPTION',
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
                    @gift_card_account_id,
                    'Debit',
                    100,
                    'TRY'),
                (
                    @credit_id,
                    @transaction_id,
                    @organization_id,
                    @corporate_account_id,
                    'Credit',
                    100,
                    'TRY');
            """,
            connection,
            transaction);
        posting.Parameters.AddWithValue("transaction_id", transactionId);
        posting.Parameters.AddWithValue("organization_id", setup.OrganizationId);
        posting.Parameters.AddWithValue(
            "idempotency_key",
            "test-consumption-" + Guid.NewGuid().ToString("N"));
        posting.Parameters.AddWithValue("intent_hash", new string('A', 64));
        posting.Parameters.AddWithValue("actor_id", Guid.CreateVersion7());
        posting.Parameters.AddWithValue("debit_id", Guid.CreateVersion7());
        posting.Parameters.AddWithValue("credit_id", Guid.CreateVersion7());
        posting.Parameters.AddWithValue(
            "gift_card_account_id",
            setup.Card.LedgerAccountId);
        posting.Parameters.AddWithValue(
            "corporate_account_id",
            corporateAccountId);
        await posting.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task<InvitationResponse> DistributeAsync(CardSetup setup)
    {
        var response = await OrganizationMember(
                fixture,
                setup.OrganizationId,
                OrganizationPermissions.GiftCardsDistribute)
            .PostAsJsonAsync(
                $"/api/v1/organizations/{setup.OrganizationId}/gift-cards/" +
                $"{setup.Card.Id}/distributions/",
                new
                {
                    contactType = "Email",
                    recipientContact =
                        $"lifecycle-{Guid.NewGuid():N}@example.com",
                    businessReference =
                        "LIFECYCLE-DIST-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey =
                        "distribution-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationResponse>())!;
    }

    private async Task<string> GetClaimTokenAsync(
        Guid organizationId,
        Guid invitationId)
    {
        var response = await OrganizationMember(
                fixture,
                organizationId,
                OrganizationPermissions.GiftCardsDistribute)
            .GetAsync(
                $"/api/v1/development/organizations/{organizationId}/" +
                $"claim-deliveries/{invitationId}");
        response.EnsureSuccessStatusCode();
        var delivery =
            (await response.Content.ReadFromJsonAsync<ClaimDeliveryResponse>())!;
        const string marker = "token=";
        var index = delivery.ClaimUrl.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return Uri.UnescapeDataString(
            delivery.ClaimUrl[(index + marker.Length)..]);
    }

    private HttpClient LifecycleClient(Guid organizationId) =>
        OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsManageLifecycle,
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

    private static object AdminRequest(string reason) =>
        new
        {
            reason,
            idempotencyKey = "lifecycle-" + Guid.NewGuid().ToString("N"),
        };

    private static object OwnerRequest() =>
        new
        {
            idempotencyKey = "owner-lifecycle-" + Guid.NewGuid().ToString("N"),
        };

    private static string OrganizationLifecycleRoute(
        Guid organizationId,
        Guid giftCardId,
        string action) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/" +
        $"lifecycle/{action}";

    private static string OrganizationHistoryRoute(
        Guid organizationId,
        Guid giftCardId) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/" +
        "lifecycle/history";

    private static string PlatformLifecycleRoute(Guid giftCardId, string action) =>
        $"/api/v1/platform/gift-cards/{giftCardId}/lifecycle/{action}";

    private static string OwnerLifecycleRoute(Guid giftCardId, string action) =>
        $"/api/v1/me/gift-cards/{giftCardId}/lifecycle/{action}";

    private static string OwnerHistoryRoute(Guid giftCardId) =>
        $"/api/v1/me/gift-cards/{giftCardId}/lifecycle/history";

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
            where account_id = @account_id
            """);
        command.Parameters.AddWithValue("account_id", accountId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string, string, string)>
        ReadCardAndInvitationStateAsync(
            ScopedSqlSession session,
            Guid giftCardId,
            Guid invitationId)
    {
        await using var command = session.Command(
            """
            select
                card.ownership_state,
                card.lifecycle_state,
                invitation.state
            from gift_cards.gift_cards card
            join distribution.invitations invitation
              on invitation.id = card.distribution_invitation_id
            where card.id = @card_id
              and invitation.id = @invitation_id
            """);
        command.Parameters.AddWithValue("card_id", giftCardId);
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    private async Task AssertTerminalCardMutationRejectedAsync(CardSetup setup)
    {
        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        await using var command = session.Command(
            """
            update gift_cards.gift_cards
            set business_reference = business_reference || '-tampered'
            where id = @id
            """);
        command.Parameters.AddWithValue("id", setup.Card.Id);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", exception.SqlState);
    }

    private async Task AssertLifecycleEventMutationRejectedAsync(CardSetup setup)
    {
        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(
                fixture,
                setup.OrganizationId);
        await using var command = session.Command(
            """
            update gift_cards.lifecycle_events
            set reason = 'tampered'
            where gift_card_id = @id
            """);
        command.Parameters.AddWithValue("id", setup.Card.Id);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", exception.SqlState);
    }

    private sealed record CardSetup(Guid OrganizationId, CardResponse Card);

    private sealed record CardResponse
    {
        public Guid Id { get; init; }

        public string LifecycleState { get; init; } = string.Empty;

        public Guid LedgerAccountId { get; init; }

        public Guid IssuanceLedgerTransactionId { get; init; }
    }

    private sealed record LifecycleOperationResponse(
        LifecycleEventResponse Event);

    private sealed record LifecycleHistoryResponse(
        CardResponse GiftCard,
        IReadOnlyList<LifecycleEventResponse> Events);

    private sealed record LifecycleEventResponse(
        Guid Id,
        string Action,
        string PreviousState,
        string NewState,
        string ActorType,
        Guid CorrelationId,
        string Reason,
        string IdempotencyKey,
        Guid? LedgerTransactionId,
        decimal? ReturnedAmount,
        string? Currency);

    private sealed record InvitationResponse(Guid Id);

    private sealed record ClaimDeliveryResponse(string ClaimUrl);

    private sealed record ClaimResponse(Guid OwnerUserId);
}
