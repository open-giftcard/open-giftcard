using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class PaymentProvisionTests(PlatformApiFixture fixture)
{
    private const string RecipientPassword = "provision recipient passphrase";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_till_reserves_value_without_posting_to_the_ledger()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var beforeEntries = await CountLedgerEntriesAsync();

        var provision = await CreateProvisionAsync(world, amount: 30m);

        Assert.Equal("Active", provision.State);
        Assert.Equal(30m, provision.Amount);
        Assert.Equal(world.StoreReference, provision.StoreReference);
        // The hold reserves value; posting happens only at confirmation.
        Assert.Equal(beforeEntries, await CountLedgerEntriesAsync());

        // Exactly two minutes, per ADR-044.
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            provision.ExpiresAtUtc - provision.CreatedAtUtc);
    }

    [Fact]
    public async Task The_exact_owner_sees_pending_active_and_confirmed_checkout_status()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var issued = await IssueCredentialsAsync(world);
        var path = $"/api/v1/me/gift-cards/{world.GiftCardId}/payment-tokens/{issued.Id}";

        using var pendingResponse = await world.Owner.GetAsync(path);
        var pendingBody = await pendingResponse.Content.ReadAsStringAsync();
        pendingResponse.EnsureSuccessStatusCode();
        var pending = JsonSerializer.Deserialize<PaymentTokenStatusResult>(pendingBody, JsonOptions)!;
        Assert.Equal("Pending", pending.State);
        Assert.Null(pending.PaymentProvisionId);
        Assert.Null(pending.Amount);

        using var provisionResponse = await PostProvisionAsync(world, issued.RawToken, amount: 30m);
        provisionResponse.EnsureSuccessStatusCode();
        var provision = (await provisionResponse.Content
            .ReadFromJsonAsync<PaymentProvisionResult>(JsonOptions))!;

        using var activeResponse = await world.Owner.GetAsync(path);
        var activeBody = await activeResponse.Content.ReadAsStringAsync();
        activeResponse.EnsureSuccessStatusCode();
        var active = JsonSerializer.Deserialize<PaymentTokenStatusResult>(activeBody, JsonOptions)!;
        Assert.Equal("Active", active.State);
        Assert.Equal(provision.Id, active.PaymentProvisionId);
        Assert.Equal(30m, active.Amount);
        Assert.Equal("TRY", active.Currency);

        _ = await ConfirmAsync(world.Pos, provision.Id, amount: 24m);

        using var confirmedResponse = await world.Owner.GetAsync(path);
        var confirmedBody = await confirmedResponse.Content.ReadAsStringAsync();
        confirmedResponse.EnsureSuccessStatusCode();
        var confirmed = JsonSerializer.Deserialize<PaymentTokenStatusResult>(
            confirmedBody,
            JsonOptions)!;
        Assert.Equal("Confirmed", confirmed.State);
        Assert.Equal(24m, confirmed.ConfirmedAmount);
        Assert.NotNull(confirmed.SettledAtUtc);

        var ownerVisibleBodies = pendingBody + activeBody + confirmedBody;
        Assert.DoesNotContain(issued.RawToken, ownerVisibleBodies, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.NumericCode, ownerVisibleBodies, StringComparison.Ordinal);

        var otherOwner = await ArrangeAsync(cardAmount: 10m);
        using var hidden = await otherOwner.Owner.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task An_active_hold_reduces_the_value_the_cardholder_can_see_and_share()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        await CreateProvisionAsync(world, amount: 40m);

        var detail = await world.Owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}",
            JsonOptions);

        Assert.NotNull(detail);
        Assert.Equal(100m, detail.Balance);
        Assert.Equal(40m, detail.ReservedBalance);
        Assert.Equal(60m, detail.AvailableBalance);

        // The cardholder cannot share value already promised to the till.
        var overlapping = await world.Owner.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/shares",
            new { amount = 70m, idempotencyKey = "provision-overlap-" + Guid.NewGuid().ToString("N") });

        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
    }

    [Fact]
    public async Task A_hold_cannot_exceed_value_already_reserved_by_a_share()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var share = await world.Owner.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/shares",
            new { amount = 80m, idempotencyKey = "provision-share-" + Guid.NewGuid().ToString("N") });
        share.EnsureSuccessStatusCode();

        var token = await IssueTokenAsync(world);
        var response = await PostProvisionAsync(world, token, amount: 30m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_credential_can_only_ever_produce_one_hold()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var token = await IssueTokenAsync(world);

        var first = await PostProvisionAsync(world, token, amount: 10m);
        var replay = await PostProvisionAsync(world, token, amount: 10m);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        // A replayed credential is refused exactly like an unknown one.
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Concurrent_scans_of_one_credential_yield_exactly_one_hold()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var token = await IssueTokenAsync(world);

        var attempts = await Task.WhenAll(
            PostProvisionAsync(world, token, amount: 10m),
            PostProvisionAsync(world, token, amount: 10m));

        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, attempts.Count(response => response.StatusCode != HttpStatusCode.Created));
    }

    [Fact]
    public async Task A_grouped_numeric_code_creates_a_hold()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var issued = await IssueCredentialsAsync(world);
        var grouped = string.Join(' ',
            issued.NumericCode[..4],
            issued.NumericCode[4..8],
            issued.NumericCode[8..]);

        var response = await PostNumericProvisionAsync(world, grouped, amount: 10m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Qr_and_numeric_forms_share_one_single_use_state()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var issued = await IssueCredentialsAsync(world);

        var numeric = await PostNumericProvisionAsync(world, issued.NumericCode, amount: 10m);
        var qrReplay = await PostProvisionAsync(world, issued.RawToken, amount: 10m);

        Assert.Equal(HttpStatusCode.Created, numeric.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, qrReplay.StatusCode);
    }

    [Fact]
    public async Task Concurrent_qr_and_numeric_presentation_yields_exactly_one_hold()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var issued = await IssueCredentialsAsync(world);

        var attempts = await Task.WhenAll(
            PostProvisionAsync(world, issued.RawToken, amount: 10m),
            PostNumericProvisionAsync(world, issued.NumericCode, amount: 10m));

        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Created));
        var observed = await Task.WhenAll(attempts.Select(async response =>
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}"));
        Assert.True(
            attempts.Count(response => response.StatusCode == HttpStatusCode.Unauthorized) == 1,
            "Expected one generic refusal; observed "
            + string.Join(" | ", observed));
    }

    [Fact]
    public async Task Exactly_one_credential_form_is_required()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var issued = await IssueCredentialsAsync(world);

        var both = await PostProvisionWithCredentialsAsync(
            world,
            issued.RawToken,
            issued.NumericCode,
            amount: 10m);
        var neither = await PostProvisionWithCredentialsAsync(
            world,
            paymentToken: null,
            paymentCode: null,
            amount: 10m);

        Assert.Equal(HttpStatusCode.Unauthorized, both.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, neither.StatusCode);
        await AssertEquivalentRefusalsAsync(both, neither);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("1234 5678 901X")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_malformed_numeric_code_is_refused_like_an_unknown_one(string? code)
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var malformed = await PostNumericProvisionAsync(world, code, amount: 10m);
        var absent = await PostNumericProvisionAsync(world, "9999 9999 9999", amount: 10m);

        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        await AssertEquivalentRefusalsAsync(malformed, absent);
    }

    [Theory]
    [InlineData("not-a-credential")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_malformed_credential_is_refused_like_an_unknown_one(string? token)
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var unknown = PaymentTokenLike();

        var malformed = await PostProvisionAsync(world, token, amount: 10m);
        var absent = await PostProvisionAsync(world, unknown, amount: 10m);

        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
    }

    [Fact]
    public async Task Cancelling_releases_the_hold_and_posts_nothing()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 25m);
        var beforeEntries = await CountLedgerEntriesAsync();

        var cancelled = await world.Pos.PostAsync(
            $"/api/v1/pos/payment-provisions/{provision.Id}/cancel",
            content: null);
        cancelled.EnsureSuccessStatusCode();
        var result = (await cancelled.Content
            .ReadFromJsonAsync<PaymentProvisionResult>(JsonOptions))!;

        Assert.Equal("Cancelled", result.State);
        Assert.NotNull(result.SettledAtUtc);
        Assert.Equal(beforeEntries, await CountLedgerEntriesAsync());

        // The released value is available again.
        var detail = await world.Owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}",
            JsonOptions);
        Assert.Equal(100m, detail!.AvailableBalance);

        // A released hold is terminal and cannot be cancelled again.
        var second = await world.Pos.PostAsync(
            $"/api/v1/pos/payment-provisions/{provision.Id}/cancel",
            content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task One_till_cannot_read_or_cancel_another_tills_hold()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 20m);
        var stranger = await ArrangePosAsync();

        var read = await stranger.GetAsync($"/api/v1/pos/payment-provisions/{provision.Id}");
        var cancel = await stranger.PostAsync(
            $"/api/v1/pos/payment-provisions/{provision.Id}/cancel",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Fact]
    public async Task A_cardholder_cannot_create_or_cancel_a_hold()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 20m);
        var token = await IssueTokenAsync(world);

        var create = await world.Owner.PostAsJsonAsync(
            "/api/v1/pos/payment-provisions",
            new { paymentToken = token, amount = 5m });
        var cancel = await world.Owner.PostAsync(
            $"/api/v1/pos/payment-provisions/{provision.Id}/cancel",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
    }

    [Fact]
    public async Task A_hold_stops_reserving_value_once_its_window_has_passed()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 50m);

        // ADR-044 fixes the window and the module validates it, so the only way
        // to reach expiry in a test is controlled ageing. Both timestamps move
        // together so the window itself stays exactly two minutes. The window is
        // immutable by design, so the ageing runs as the schema owner with that
        // trigger suspended rather than by weakening the guarantee.
        await using (var connection = new NpgsqlConnection(fixture.MigratorConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                "select set_config('app.is_platform_operator', 'true', false)");
            await ExecuteAsync(
                connection,
                "alter table payments.payment_provisions "
                + "disable trigger payments_provision_identity_immutable");

            await using (var command = new NpgsqlCommand(
                """
                update payments.payment_provisions
                set created_at_utc = created_at_utc - interval '10 minutes',
                    expires_at_utc = expires_at_utc - interval '10 minutes'
                where id = @id
                """,
                connection))
            {
                command.Parameters.AddWithValue("id", provision.Id);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await ExecuteAsync(
                connection,
                "alter table payments.payment_provisions "
                + "enable trigger payments_provision_identity_immutable");
        }

        // Availability is clock-derived, so the value is free before any sweep.
        var detail = await world.Owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}",
            JsonOptions);
        Assert.Equal(0m, detail!.ReservedBalance);
        Assert.Equal(100m, detail.AvailableBalance);
    }

    [Fact]
    public async Task Partial_confirmation_posts_once_releases_the_remainder_and_retries_forever()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 30m);

        var confirmed = await ConfirmAsync(world.Pos, provision.Id, 20m);

        Assert.Equal("Confirmed", confirmed.State);
        Assert.Equal(30m, confirmed.Amount);
        Assert.Equal(20m, confirmed.ConfirmedAmount);
        Assert.NotNull(confirmed.SettledAtUtc);
        Assert.NotNull(confirmed.RedemptionLedgerTransactionId);

        var detail = await world.Owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}",
            JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal(80m, detail.Balance);
        Assert.Equal(0m, detail.ReservedBalance);
        Assert.Equal(80m, detail.AvailableBalance);

        var entries = await ReadRedemptionEntriesAsync(
            confirmed.RedemptionLedgerTransactionId!.Value);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, item =>
            item.AccountType == "GiftCardValue" &&
            item.Direction == "Debit" &&
            item.Amount == 20m);
        Assert.Contains(entries, item =>
            item.AccountType == "PlatformRedemptionSettlement" &&
            item.Direction == "Credit" &&
            item.Amount == 20m);

        var retry = await ConfirmAsync(world.Pos, provision.Id, 20m);
        Assert.Equal(confirmed.RedemptionLedgerTransactionId, retry.RedemptionLedgerTransactionId);
        Assert.Equal(entries.Count, (await ReadRedemptionEntriesAsync(
            retry.RedemptionLedgerTransactionId!.Value)).Count);

        var changedIntent = await PostConfirmationAsync(world.Pos, provision.Id, 19m);
        Assert.Equal(HttpStatusCode.Conflict, changedIntent.StatusCode);
    }

    [Fact]
    public async Task Confirmation_above_the_hold_is_refused_without_consuming_it()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 30m);

        var response = await PostConfirmationAsync(world.Pos, provision.Id, 30.0001m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var unchanged = await world.Pos.GetFromJsonAsync<PaymentProvisionResult>(
            $"/api/v1/pos/payment-provisions/{provision.Id}",
            JsonOptions);
        Assert.NotNull(unchanged);
        Assert.Equal("Active", unchanged.State);
        Assert.Null(unchanged.ConfirmedAmount);
        Assert.Null(unchanged.RedemptionLedgerTransactionId);
    }

    [Fact]
    public async Task Concurrent_confirmation_has_one_financial_effect()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 25m);

        var responses = await Task.WhenAll(
            PostConfirmationAsync(world.Pos, provision.Id, 25m),
            PostConfirmationAsync(world.Pos, provision.Id, 25m));

        Assert.Contains(responses, response => response.IsSuccessStatusCode);
        Assert.All(
            responses.Where(response => !response.IsSuccessStatusCode),
            response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));

        // A SERIALIZABLE contender may be told to retry after its older
        // snapshot loses. The retry is the important ambiguity boundary: it
        // must return the one committed effect, never post a second one.
        var settled = await ConfirmAsync(world.Pos, provision.Id, 25m);
        Assert.NotNull(settled.RedemptionLedgerTransactionId);
        Assert.Equal(
            2,
            (await ReadRedemptionEntriesAsync(
                settled.RedemptionLedgerTransactionId.Value)).Count);

        foreach (var response in responses.Where(item => item.IsSuccessStatusCode))
        {
            var result = await response.Content
                .ReadFromJsonAsync<PaymentProvisionResult>(JsonOptions);
            Assert.Equal(
                settled.RedemptionLedgerTransactionId,
                result!.RedemptionLedgerTransactionId);
        }
    }

    [Fact]
    public async Task Confirmation_and_share_claim_cannot_overspend_the_same_card()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var shareResponse = await world.Owner.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/shares",
            new
            {
                amount = 40m,
                idempotencyKey = "payment-share-create-" + Guid.NewGuid().ToString("N"),
            });
        shareResponse.EnsureSuccessStatusCode();
        var share = (await shareResponse.Content
            .ReadFromJsonAsync<CreatedGiftCardShareResult>(JsonOptions))!;
        var provision = await CreateProvisionAsync(world, amount: 60m);

        var recipient = fixture.Factory.CreateClient();
        recipient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.CreateAccessToken(Guid.CreateVersion7()));
        var claimToken = ExtractToken(share.ClaimUrl);
        var claimKey = "payment-share-claim-" + Guid.NewGuid().ToString("N");
        var attempts = await Task.WhenAll(
            PostConfirmationAsync(world.Pos, provision.Id, 60m),
            recipient.PostAsJsonAsync(
                "/api/v1/share-claims",
                new { claimToken, pin = share.Pin, idempotencyKey = claimKey }));

        Assert.All(
            attempts,
            response => Assert.True(
                response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict));

        _ = await ConfirmAsync(world.Pos, provision.Id, 60m);
        var claimedResponse = await recipient.PostAsJsonAsync(
            "/api/v1/share-claims",
            new { claimToken, pin = share.Pin, idempotencyKey = claimKey });
        claimedResponse.EnsureSuccessStatusCode();
        var claimed = await claimedResponse.Content
            .ReadFromJsonAsync<ClaimedGiftCardShareResult>(JsonOptions);
        Assert.Equal(40m, claimed!.ChildGiftCard.FundedAmount);

        var source = await world.Owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}",
            JsonOptions);
        Assert.NotNull(source);
        Assert.Equal(0m, source.Balance);
        Assert.Equal(0m, source.ReservedBalance);
        Assert.Equal(0m, source.AvailableBalance);
    }

    [Fact]
    public async Task Foreign_cancelled_and_expired_provisions_cannot_be_confirmed()
    {
        var foreignWorld = await ArrangeAsync(cardAmount: 100m);
        var foreignProvision = await CreateProvisionAsync(foreignWorld, amount: 10m);
        var stranger = await ArrangePosAsync();
        var foreign = await PostConfirmationAsync(stranger, foreignProvision.Id, 10m);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var cancelledWorld = await ArrangeAsync(cardAmount: 100m);
        var cancelledProvision = await CreateProvisionAsync(cancelledWorld, amount: 10m);
        (await cancelledWorld.Pos.PostAsync(
            $"/api/v1/pos/payment-provisions/{cancelledProvision.Id}/cancel",
            content: null)).EnsureSuccessStatusCode();
        var cancelled = await PostConfirmationAsync(
            cancelledWorld.Pos,
            cancelledProvision.Id,
            10m);
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);

        var expiredWorld = await ArrangeAsync(cardAmount: 100m);
        var expiredProvision = await CreateProvisionAsync(expiredWorld, amount: 10m);
        await AgeProvisionAsync(expiredProvision.Id);
        var expired = await PostConfirmationAsync(expiredWorld.Pos, expiredProvision.Id, 10m);
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
    }

    [Fact]
    public async Task Redemption_is_visible_in_financial_owner_and_reconciliation_views()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 30m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 24m);
        var financial = OrganizationMember(
            fixture,
            world.OrganizationId,
            OrganizationPermissions.CorporateCreditsView,
            OrganizationPermissions.GiftCardsView);

        var summary = await financial.GetFromJsonAsync<OrganizationFinancialSummary>(
            $"/api/v1/organizations/{world.OrganizationId}/reports/financial-summary",
            JsonOptions);
        Assert.Equal(24m, Assert.Single(summary!.Currencies).Spent);

        var history = await financial.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/organizations/{world.OrganizationId}/reports/financial-history",
            JsonOptions);
        Assert.Contains(history!.Items, item =>
            item.Category == "Redemption" &&
            item.Operation == "Confirmed" &&
            item.Amount == 24m &&
            item.GiftCardId == world.GiftCardId);

        var ownerHistory = await world.Owner.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/history",
            JsonOptions);
        Assert.Contains(ownerHistory!.Items, item =>
            item.Operation == "gift_card.redemption" &&
            item.Amount == 24m &&
            item.FinancialDirection == "Debit");

        var reconciliation = await financial
            .GetFromJsonAsync<OrganizationReconciliationResult>(
                $"/api/v1/organizations/{world.OrganizationId}/reports/reconciliation",
                JsonOptions);
        Assert.NotNull(reconciliation);
        Assert.True(reconciliation.IsConsistent);
        Assert.Empty(reconciliation.Findings);
    }

    [Fact]
    public async Task Multiple_partial_refunds_restore_value_and_safe_retry_returns_one_effect()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 80m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 80m);

        var first = await RefundAsync(world.Pos, provision.Id, 25m, "refund-part-one");
        var retry = await RefundAsync(world.Pos, provision.Id, 25m, "refund-part-one");
        var second = await RefundAsync(world.Pos, provision.Id, 55m, "refund-part-two");

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.RefundLedgerTransactionId, retry.RefundLedgerTransactionId);
        Assert.Equal(55m, first.RemainingRefundableAmount);
        Assert.Equal(0m, second.RemainingRefundableAmount);
        Assert.Equal(2, await CountRefundsAsync(provision.Id));

        var detail = await world.Owner.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}", JsonOptions);
        Assert.Equal(100m, detail!.Balance);

        var entries = await ReadRedemptionEntriesAsync(second.RefundLedgerTransactionId);
        Assert.Contains(entries, item => item.AccountType == "PlatformRedemptionSettlement" &&
            item.Direction == "Debit" && item.Amount == 55m);
        Assert.Contains(entries, item => item.AccountType == "GiftCardValue" &&
            item.Direction == "Credit" && item.Amount == 55m);
    }

    [Fact]
    public async Task Refund_cap_is_serialized_and_changed_idempotent_intent_is_refused()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 100m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 100m);

        var attempts = await Task.WhenAll(
            PostRefundAsync(world.Pos, provision.Id, 60m, "refund-concurrent-a"),
            PostRefundAsync(world.Pos, provision.Id, 60m, "refund-concurrent-b"));
        Assert.Equal(1, attempts.Count(response => response.IsSuccessStatusCode));
        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        var successfulKey = attempts[0].IsSuccessStatusCode
            ? "refund-concurrent-a"
            : "refund-concurrent-b";
        var changed = await PostRefundAsync(
            world.Pos, provision.Id, 59m, successfulKey);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, await CountRefundsAsync(provision.Id));
    }

    [Fact]
    public async Task Refund_is_limited_to_original_pos_client_and_card_lifecycle_boundary()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 40m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 40m);

        var stranger = await ArrangePosAsync();
        var foreign = await PostRefundAsync(stranger, provision.Id, 5m, "foreign-refund");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var suspend = await world.Owner.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/lifecycle/suspend",
            new { reason = "Refund investigation.", idempotencyKey = "suspend-refund-" + Guid.NewGuid().ToString("N") });
        suspend.EnsureSuccessStatusCode();
        var siblingTerminal = await ArrangeSiblingTerminalAsync(world);
        _ = await RefundAsync(siblingTerminal, provision.Id, 10m, "suspended-refund");

        var platform = PlatformOperator(fixture, PlatformPermissions.GiftCardsManageLifecycle);
        var cancel = await platform.PostAsJsonAsync(
            $"/api/v1/platform/gift-cards/{world.GiftCardId}/lifecycle/cancel",
            new { reason = "Terminal refund boundary.", idempotencyKey = "cancel-refund-" + Guid.NewGuid().ToString("N") });
        cancel.EnsureSuccessStatusCode();
        var terminal = await PostRefundAsync(world.Pos, provision.Id, 5m, "terminal-refund");
        Assert.Equal(HttpStatusCode.Conflict, terminal.StatusCode);
    }

    [Fact]
    public async Task Refunds_are_explicit_in_summary_history_owner_history_and_reconciliation()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 30m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 30m);
        _ = await RefundAsync(world.Pos, provision.Id, 12m, "reporting-refund");
        var financial = OrganizationMember(
            fixture, world.OrganizationId,
            OrganizationPermissions.CorporateCreditsView,
            OrganizationPermissions.GiftCardsView);

        var summary = await financial.GetFromJsonAsync<OrganizationFinancialSummary>(
            $"/api/v1/organizations/{world.OrganizationId}/reports/financial-summary", JsonOptions);
        var currency = Assert.Single(summary!.Currencies);
        Assert.Equal(30m, currency.Spent);
        Assert.Equal(12m, currency.Refunded);
        Assert.Equal(18m, currency.NetSpent);

        var history = await financial.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/organizations/{world.OrganizationId}/reports/financial-history", JsonOptions);
        Assert.Contains(history!.Items, item => item.Category == "Refund" &&
            item.Operation == "Refunded" && item.Amount == 12m);
        var ownerHistory = await world.Owner.GetFromJsonAsync<FinancialHistoryPage>(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/history", JsonOptions);
        Assert.Contains(ownerHistory!.Items, item => item.Operation == "gift_card.refund" &&
            item.Amount == 12m && item.FinancialDirection == "Credit");
        var reconciliation = await financial.GetFromJsonAsync<OrganizationReconciliationResult>(
            $"/api/v1/organizations/{world.OrganizationId}/reports/reconciliation", JsonOptions);
        Assert.True(reconciliation!.IsConsistent,
            string.Join(Environment.NewLine, reconciliation.Findings.Select(item => item.Code)));
    }

    [Fact]
    public async Task Committed_refunds_are_append_only_at_the_database_boundary()
    {
        var world = await ArrangeAsync(cardAmount: 50m);
        var provision = await CreateProvisionAsync(world, amount: 20m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 20m);
        var refund = await RefundAsync(world.Pos, provision.Id, 5m, "immutable-refund");

        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "select set_config('app.is_platform_operator', 'true', false)");
        await using var update = new NpgsqlCommand(
            "update payments.payment_refunds set amount = 4 where id = @id", connection);
        update.Parameters.AddWithValue("id", refund.Id);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());

        await using var delete = new NpgsqlCommand(
            "delete from payments.payment_refunds where id = @id", connection);
        delete.Parameters.AddWithValue("id", refund.Id);
        Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        Assert.Equal(1, await CountRefundsAsync(provision.Id));
    }

    [Fact]
    public async Task Reconciliation_reports_settlement_divergence_without_repairing_it()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 30m);
        _ = await ConfirmAsync(world.Pos, provision.Id, 24m);
        await SetConfirmedAmountForReconciliationTestAsync(provision.Id, 23m);

        try
        {
            var operatorClient = PlatformOperator(
                fixture,
                PlatformPermissions.CorporateCreditsView,
                PlatformPermissions.GiftCardsView);
            var reconciliation = await operatorClient
                .GetFromJsonAsync<OrganizationReconciliationResult>(
                    $"/api/v1/organizations/{world.OrganizationId}/reports/reconciliation",
                    JsonOptions);

            Assert.NotNull(reconciliation);
            Assert.False(reconciliation.IsConsistent);
            Assert.Contains(reconciliation.Findings, finding =>
                finding.Code == "ledger.redemption_settlement.mismatch" &&
                finding.Currency == "TRY");
            Assert.Equal(23m, await ReadConfirmedAmountAsync(provision.Id));
        }
        finally
        {
            await SetConfirmedAmountForReconciliationTestAsync(provision.Id, 24m);
        }
    }

    [Fact]
    public async Task Platform_payment_report_filters_totals_and_receipt_are_authoritative()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var provision = await CreateProvisionAsync(world, amount: 80m);
        var confirmed = await ConfirmAsync(world.Pos, provision.Id, 80m);
        var first = await RefundAsync(world.Pos, provision.Id, 25m, "report-first-refund");
        var second = await RefundAsync(world.Pos, provision.Id, 55m, "report-second-refund");
        var reports = PlatformOperator(fixture, PlatformPermissions.PaymentsView);

        var page = await reports.GetFromJsonAsync<PaymentReportPage>(
            $"/api/v1/platform/reports/payments?limit=10" +
            $"&storeReference={world.StoreReference}" +
            $"&fundingOrganizationId={world.OrganizationId}" +
            "&state=confirmed&currency=try&reference=SALE-",
            JsonOptions);

        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(provision.Id, item.PaymentProvisionId);
        Assert.Equal(world.OrganizationId, item.FundingOrganizationId);
        Assert.Equal(world.PosClientId, item.PosClientId);
        Assert.Equal(world.StoreReference, item.StoreReference);
        Assert.Equal(80m, item.ProvisionedAmount);
        Assert.Equal(80m, item.ConfirmedAmount);
        Assert.Equal(80m, item.RefundedAmount);
        Assert.Equal(0m, item.NetAmount);
        Assert.True(item.IsFullyReversed);
        Assert.Equal(2, item.RefundCount);
        Assert.Equal(confirmed.RedemptionLedgerTransactionId, item.RedemptionLedgerTransactionId);
        Assert.Equal(1, page.TotalMatchingPayments);
        var pageTotals = Assert.Single(page.PageTotals);
        var matchingTotals = Assert.Single(page.MatchingTotals);
        Assert.Equal("TRY", matchingTotals.Currency);
        Assert.Equal(1, matchingTotals.PaymentCount);
        Assert.Equal(1, matchingTotals.ConfirmedPaymentCount);
        Assert.Equal(2, matchingTotals.RefundCount);
        Assert.Equal(1, matchingTotals.FullyReversedPaymentCount);
        Assert.Equal(80m, matchingTotals.ProvisionedAmount);
        Assert.Equal(80m, matchingTotals.ConfirmedAmount);
        Assert.Equal(80m, matchingTotals.RefundedAmount);
        Assert.Equal(0m, matchingTotals.NetAmount);
        Assert.Equal(matchingTotals, pageTotals);

        var boundedRoute = $"/api/v1/platform/reports/payments?limit=10" +
            $"&posClientId={item.PosClientId}" +
            $"&posTerminalId={item.PosTerminalId}" +
            $"&occurredFromUtc={Uri.EscapeDataString(item.CreatedAtUtc.AddMinutes(-1).ToString("O"))}" +
            $"&occurredBeforeUtc={Uri.EscapeDataString(item.CreatedAtUtc.AddMinutes(1).ToString("O"))}";
        Assert.Single((await reports.GetFromJsonAsync<PaymentReportPage>(
            boundedRoute, JsonOptions))!.Items);
        Assert.Empty((await reports.GetFromJsonAsync<PaymentReportPage>(
            boundedRoute.Replace(
                item.PosTerminalId.ToString(),
                Guid.CreateVersion7().ToString(),
                StringComparison.Ordinal),
            JsonOptions))!.Items);

        var receipt = await reports.GetFromJsonAsync<PaymentReceiptReport>(
            $"/api/v1/platform/reports/payments/{provision.Id}",
            JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(item, receipt.Payment);
        Assert.Collection(
            receipt.Refunds,
            refund =>
            {
                Assert.Equal(first.Id, refund.RefundId);
                Assert.Equal(first.RefundLedgerTransactionId, refund.RefundLedgerTransactionId);
                Assert.Equal(25m, refund.Amount);
            },
            refund =>
            {
                Assert.Equal(second.Id, refund.RefundId);
                Assert.Equal(second.RefundLedgerTransactionId, refund.RefundLedgerTransactionId);
                Assert.Equal(55m, refund.Amount);
            });
    }

    [Fact]
    public async Task Platform_payment_report_is_permission_protected_and_missing_receipts_are_not_found()
    {
        var world = await ArrangeAsync(cardAmount: 40m);
        var provision = await CreateProvisionAsync(world, amount: 10m);

        var noPermission = await PlatformOperator(fixture)
            .GetAsync("/api/v1/platform/reports/payments");
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);

        var organizationUser = OrganizationMember(
            fixture,
            world.OrganizationId,
            OrganizationPermissions.CorporateCreditsView,
            OrganizationPermissions.GiftCardsView);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await organizationUser.GetAsync("/api/v1/platform/reports/payments")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await world.Pos.GetAsync("/api/v1/platform/reports/payments")).StatusCode);

        var reports = PlatformOperator(fixture, PlatformPermissions.PaymentsView);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await reports.GetAsync(
                $"/api/v1/platform/reports/payments/{Guid.CreateVersion7()}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await reports.GetAsync(
                $"/api/v1/platform/reports/payments/{provision.Id}")).StatusCode);
    }

    [Fact]
    public async Task Platform_payment_report_cursor_is_stable_and_bound_to_filters()
    {
        var world = await ArrangeAsync(cardAmount: 100m);
        var first = await CreateProvisionAsync(world, amount: 10m);
        _ = await ConfirmAsync(world.Pos, first.Id, 10m);
        var second = await CreateProvisionAsync(world, amount: 20m);
        _ = await ConfirmAsync(world.Pos, second.Id, 20m);
        var reports = PlatformOperator(fixture, PlatformPermissions.PaymentsView);
        var route = $"/api/v1/platform/reports/payments?limit=1&storeReference={world.StoreReference}";

        var pageOne = await reports.GetFromJsonAsync<PaymentReportPage>(route, JsonOptions);
        Assert.NotNull(pageOne);
        Assert.Single(pageOne.Items);
        Assert.NotNull(pageOne.NextCursor);
        Assert.Equal(2, pageOne.TotalMatchingPayments);
        Assert.Equal(30m, Assert.Single(pageOne.MatchingTotals).ConfirmedAmount);

        var pageTwo = await reports.GetFromJsonAsync<PaymentReportPage>(
            route + $"&cursor={Uri.EscapeDataString(pageOne.NextCursor)}",
            JsonOptions);
        Assert.NotNull(pageTwo);
        Assert.Single(pageTwo.Items);
        Assert.Null(pageTwo.NextCursor);
        Assert.NotEqual(
            pageOne.Items[0].PaymentProvisionId,
            pageTwo.Items[0].PaymentProvisionId);

        var changedFilter = await reports.GetAsync(
            route + $"&state=confirmed&cursor={Uri.EscapeDataString(pageOne.NextCursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, changedFilter.StatusCode);
    }

    [Fact]
    public async Task Development_OpenAPI_exposes_provisioning_without_credentials()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/pos/payment-provisions",
            out var createPath));
        Assert.True(createPath.TryGetProperty("post", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/pos/payment-provisions/{provisionId}/confirm",
            out var confirmPath));
        Assert.True(confirmPath.TryGetProperty("post", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/pos/payment-provisions/{provisionId}/refunds",
            out var refundPath));
        Assert.True(refundPath.TryGetProperty("post", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/platform/reports/payments",
            out var paymentReportPath));
        Assert.True(paymentReportPath.TryGetProperty("get", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/platform/reports/payments/{paymentProvisionId}",
            out var receiptReportPath));
        Assert.True(receiptReportPath.TryGetProperty("get", out _));

        var result = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("PaymentProvisionResult")
            .GetProperty("properties");
        Assert.False(result.TryGetProperty("paymentToken", out _));
        Assert.False(result.TryGetProperty("paymentCode", out _));
        Assert.False(result.TryGetProperty("secretHash", out _));
        Assert.True(result.TryGetProperty("confirmedAmount", out _));
        Assert.True(result.TryGetProperty("redemptionLedgerTransactionId", out _));
        var refundResult = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("PaymentRefundResult").GetProperty("properties");
        Assert.True(refundResult.TryGetProperty("remainingRefundableAmount", out _));
        Assert.False(refundResult.TryGetProperty("paymentToken", out _));
        var reportItem = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("PaymentReportItem").GetProperty("properties");
        Assert.True(reportItem.TryGetProperty("isFullyReversed", out _));
        Assert.False(reportItem.TryGetProperty("ownerUserId", out _));
        Assert.False(reportItem.TryGetProperty("paymentToken", out _));
        Assert.False(reportItem.TryGetProperty("paymentCode", out _));
        Assert.False(reportItem.TryGetProperty("idempotencyKey", out _));
    }

    private static string PaymentTokenLike() =>
        $"{Guid.CreateVersion7():N}.{Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static async Task AssertEquivalentRefusalsAsync(
        HttpResponseMessage first,
        HttpResponseMessage second)
    {
        var firstProblem = System.Text.Json.Nodes.JsonNode
            .Parse(await first.Content.ReadAsStringAsync())!.AsObject();
        var secondProblem = System.Text.Json.Nodes.JsonNode
            .Parse(await second.Content.ReadAsStringAsync())!.AsObject();
        firstProblem.Remove("correlationId");
        secondProblem.Remove("correlationId");
        Assert.Equal(firstProblem.ToJsonString(), secondProblem.ToJsonString());
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AgeProvisionAsync(Guid provisionId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "select set_config('app.is_platform_operator', 'true', false)");
        await ExecuteAsync(
            connection,
            "alter table payments.payment_provisions disable trigger "
            + "payments_provision_identity_immutable");
        await using (var command = new NpgsqlCommand(
            """
            update payments.payment_provisions
            set created_at_utc = created_at_utc - interval '10 minutes',
                expires_at_utc = expires_at_utc - interval '10 minutes'
            where id = @id
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", provisionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await ExecuteAsync(
            connection,
            "alter table payments.payment_provisions enable trigger "
            + "payments_provision_identity_immutable");
    }

    private async Task<List<RedemptionEntry>> ReadRedemptionEntriesAsync(Guid transactionId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "select set_config('app.is_platform_operator', 'true', false)");
        await using var command = new NpgsqlCommand(
            """
            select account.type, entry.direction, entry.amount
            from ledger.entries entry
            join ledger.accounts account on account.id = entry.account_id
            where entry.transaction_id = @transaction_id
            order by entry.direction
            """,
            connection);
        command.Parameters.AddWithValue("transaction_id", transactionId);
        var entries = new List<RedemptionEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new RedemptionEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDecimal(2)));
        }

        return entries;
    }

    private async Task SetConfirmedAmountForReconciliationTestAsync(
        Guid provisionId,
        decimal amount)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "select set_config('app.is_platform_operator', 'true', false)");
        await ExecuteAsync(
            connection,
            "alter table payments.payment_provisions disable trigger "
            + "payments_provision_settlement_final");
        try
        {
            await using var command = new NpgsqlCommand(
                """
                update payments.payment_provisions
                set confirmed_amount = @amount
                where id = @id
                """,
                connection);
            command.Parameters.AddWithValue("amount", amount);
            command.Parameters.AddWithValue("id", provisionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "alter table payments.payment_provisions enable trigger "
                + "payments_provision_settlement_final");
        }
    }

    private async Task<decimal> ReadConfirmedAmountAsync(Guid provisionId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "select set_config('app.is_platform_operator', 'true', false)");
        await using var command = new NpgsqlCommand(
            "select confirmed_amount from payments.payment_provisions where id = @id",
            connection);
        command.Parameters.AddWithValue("id", provisionId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static Task<HttpResponseMessage> PostConfirmationAsync(
        HttpClient pos,
        Guid provisionId,
        decimal amount) =>
        pos.PostAsJsonAsync(
            $"/api/v1/pos/payment-provisions/{provisionId}/confirm",
            new { amount });

    private static Task<HttpResponseMessage> PostRefundAsync(
        HttpClient pos,
        Guid provisionId,
        decimal amount,
        string idempotencyKey) =>
        pos.PostAsJsonAsync(
            $"/api/v1/pos/payment-provisions/{provisionId}/refunds",
            new
            {
                amount,
                idempotencyKey,
                posTransactionReference = "RETURN-" + idempotencyKey,
                reason = "Customer return",
            });

    private static async Task<PaymentRefundResult> RefundAsync(
        HttpClient pos,
        Guid provisionId,
        decimal amount,
        string idempotencyKey)
    {
        var response = await PostRefundAsync(pos, provisionId, amount, idempotencyKey);
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PaymentRefundResult>(JsonOptions))!;
    }

    private async Task<long> CountRefundsAsync(Guid provisionId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "select set_config('app.is_platform_operator', 'true', false)");
        await using var command = new NpgsqlCommand(
            "select count(*) from payments.payment_refunds where payment_provision_id = @id",
            connection);
        command.Parameters.AddWithValue("id", provisionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<PaymentProvisionResult> ConfirmAsync(
        HttpClient pos,
        Guid provisionId,
        decimal amount)
    {
        var response = await PostConfirmationAsync(pos, provisionId, amount);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content
            .ReadFromJsonAsync<PaymentProvisionResult>(JsonOptions))!;
    }

    private async Task<long> CountLedgerEntriesAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        // Ledger is behind forced RLS, so the count needs the controlled
        // platform read path or it would trivially be zero either side.
        await ExecuteAsync(connection, "select set_config('app.is_platform_operator', 'true', false)");
        await using var command = new NpgsqlCommand("select count(*) from ledger.entries", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<PaymentProvisionResult> CreateProvisionAsync(
        World world,
        decimal amount)
    {
        var token = await IssueTokenAsync(world);
        var response = await PostProvisionAsync(world, token, amount);
        // Surface the refusal reason: every failure here is a bare status code
        // otherwise, which is the point of the contract but useless in arrange.
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.Headers.WwwAuthenticate} "
            + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaymentProvisionResult>(JsonOptions))!;
    }

    private static async Task<string> IssueTokenAsync(World world)
        => (await IssueCredentialsAsync(world)).RawToken;

    private static async Task<IssuedPaymentTokenResult> IssueCredentialsAsync(World world)
    {
        var response = await world.Owner.PostAsync(
            $"/api/v1/me/gift-cards/{world.GiftCardId}/payment-tokens",
            content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<IssuedPaymentTokenResult>(JsonOptions))!;
    }

    private static Task<HttpResponseMessage> PostProvisionAsync(
        World world,
        string? token,
        decimal amount) =>
        world.Pos.PostAsJsonAsync(
            "/api/v1/pos/payment-provisions",
            new
            {
                paymentToken = token,
                amount,
                posTransactionReference = "SALE-" + Guid.NewGuid().ToString("N")[..8],
            });

    private static Task<HttpResponseMessage> PostNumericProvisionAsync(
        World world,
        string? paymentCode,
        decimal amount) =>
        PostProvisionWithCredentialsAsync(
            world,
            paymentToken: null,
            paymentCode,
            amount);

    private static Task<HttpResponseMessage> PostProvisionWithCredentialsAsync(
        World world,
        string? paymentToken,
        string? paymentCode,
        decimal amount) =>
        world.Pos.PostAsJsonAsync(
            "/api/v1/pos/payment-provisions",
            new
            {
                paymentToken,
                paymentCode,
                amount,
                posTransactionReference = "SALE-" + Guid.NewGuid().ToString("N")[..8],
            });

    private async Task<HttpClient> ArrangePosAsync()
    {
        var admin = PlatformOperator(fixture, PlatformPermissions.PosClientsManage);
        var clientResponse = await admin.PostAsJsonAsync(
            "/api/v1/pos/clients",
            new
            {
                code = "POS-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                displayName = "Provision Till",
            });
        clientResponse.EnsureSuccessStatusCode();
        var client = (await clientResponse.Content
            .ReadFromJsonAsync<RegisteredPosClientResult>(JsonOptions))!;

        var terminalResponse = await admin.PostAsJsonAsync(
            $"/api/v1/pos/clients/{client.Id}/terminals",
            new
            {
                code = "TILL-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                storeReference = "STORE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            });
        terminalResponse.EnsureSuccessStatusCode();
        var terminal = (await terminalResponse.Content
            .ReadFromJsonAsync<PosTerminalResult>(JsonOptions))!;

        var tokenResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = client.Secret,
                terminalCode = terminal.Code,
            });
        tokenResponse.EnsureSuccessStatusCode();
        var access = (await tokenResponse.Content
            .ReadFromJsonAsync<PosAccessTokenResult>(JsonOptions))!;

        var pos = fixture.Factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", access.AccessToken);
        return pos;
    }

    private async Task<HttpClient> ArrangeSiblingTerminalAsync(World world)
    {
        var admin = PlatformOperator(fixture, PlatformPermissions.PosClientsManage);
        var terminalResponse = await admin.PostAsJsonAsync(
            $"/api/v1/pos/clients/{world.PosClientId}/terminals",
            new
            {
                code = "TILL-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                storeReference = "STORE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            });
        terminalResponse.EnsureSuccessStatusCode();
        var terminal = (await terminalResponse.Content
            .ReadFromJsonAsync<PosTerminalResult>(JsonOptions))!;
        var tokenResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = world.PosClientCode,
                clientSecret = world.PosClientSecret,
                terminalCode = terminal.Code,
            });
        tokenResponse.EnsureSuccessStatusCode();
        var access = (await tokenResponse.Content
            .ReadFromJsonAsync<PosAccessTokenResult>(JsonOptions))!;
        var pos = fixture.Factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", access.AccessToken);
        return pos;
    }

    private sealed record World(
        HttpClient Owner,
        HttpClient Pos,
        Guid OrganizationId,
        Guid GiftCardId,
        string StoreReference,
        Guid PosClientId,
        string PosClientCode,
        string PosClientSecret);

    private sealed record RedemptionEntry(
        string AccountType,
        string Direction,
        decimal Amount);

    private async Task<World> ArrangeAsync(decimal cardAmount)
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, cardAmount * 4);
        var card = await IssueAsync(organizationId, cardAmount);
        var contact = $"provision.{Guid.NewGuid():N}@example.test";
        await DistributeAndClaimAsync(organizationId, card.Id, contact);

        var login = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = contact, password = RecipientPassword });
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions))!;
        var owner = fixture.Factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var admin = PlatformOperator(fixture, PlatformPermissions.PosClientsManage);
        var clientResponse = await admin.PostAsJsonAsync(
            "/api/v1/pos/clients",
            new
            {
                code = "POS-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                displayName = "Provision Till",
            });
        clientResponse.EnsureSuccessStatusCode();
        var posClient = (await clientResponse.Content
            .ReadFromJsonAsync<RegisteredPosClientResult>(JsonOptions))!;
        var terminalResponse = await admin.PostAsJsonAsync(
            $"/api/v1/pos/clients/{posClient.Id}/terminals",
            new
            {
                code = "TILL-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                storeReference = "STORE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            });
        terminalResponse.EnsureSuccessStatusCode();
        var terminal = (await terminalResponse.Content
            .ReadFromJsonAsync<PosTerminalResult>(JsonOptions))!;
        var tokenResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = posClient.Code,
                clientSecret = posClient.Secret,
                terminalCode = terminal.Code,
            });
        tokenResponse.EnsureSuccessStatusCode();
        var access = (await tokenResponse.Content
            .ReadFromJsonAsync<PosAccessTokenResult>(JsonOptions))!;
        var pos = fixture.Factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", access.AccessToken);

        return new World(
            owner, pos, organizationId, card.Id, terminal.StoreReference,
            posClient.Id, posClient.Code, posClient.Secret);
    }

    private async Task AllocateAsync(Guid organizationId, decimal amount)
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
                    businessReference = "PROVISION-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "provision-fund-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private async Task<GiftCardResult> IssueAsync(Guid organizationId, decimal amount)
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
                    expiresAtUtc = DateTimeOffset.UtcNow.AddYears(1),
                    isTransferable = true,
                    isDivisible = true,
                    businessReference = "PROVISION-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "provision-card-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GiftCardResult>(JsonOptions))!;
    }

    private async Task DistributeAndClaimAsync(
        Guid organizationId,
        Guid giftCardId,
        string contact)
    {
        var distributor = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsDistribute);
        var distribution = await distributor.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/distributions/",
            new
            {
                contactType = "Email",
                recipientContact = contact,
                businessReference = "PROVISION-DIST-" + Guid.NewGuid().ToString("N"),
                idempotencyKey = "provision-dist-" + Guid.NewGuid().ToString("N"),
            });
        distribution.EnsureSuccessStatusCode();
        var invitation = (await distribution.Content
            .ReadFromJsonAsync<InvitationIdResponse>(JsonOptions))!;
        var delivery = await distributor.GetFromJsonAsync<DeliveryUrlResponse>(
            $"/api/v1/development/organizations/{organizationId}/claim-deliveries/{invitation.Id}",
            JsonOptions);

        var claim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken = ExtractToken(delivery!.ClaimUrl),
                password = RecipientPassword,
                idempotencyKey = "provision-claim-" + Guid.NewGuid().ToString("N"),
            });
        claim.EnsureSuccessStatusCode();
    }

    private static string ExtractToken(string claimUrl)
    {
        const string marker = "token=";
        var index = claimUrl.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return Uri.UnescapeDataString(claimUrl[(index + marker.Length)..]);
    }

    private sealed record LoginResponse(string AccessToken);

    private sealed record InvitationIdResponse(Guid Id);

    private sealed record DeliveryUrlResponse(string ClaimUrl);
}
