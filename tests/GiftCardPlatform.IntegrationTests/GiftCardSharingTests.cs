using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class GiftCardSharingTests(PlatformApiFixture fixture)
{
    private const string RecipientPassword = "recipient sharing passphrase";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Development_OpenAPI_exposes_protected_sharing_without_persisted_secrets()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty(
            "/api/v1/me/gift-cards/{giftCardId}/shares",
            out var createPath));
        Assert.True(createPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/me/shares", out var listPath));
        Assert.True(listPath.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty("/api/v1/share-claims", out var claimPath));
        Assert.True(claimPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty(
            "/api/v1/me/gift-cards/{giftCardId}/share-invitations",
            out var directCreatePath));
        Assert.True(directCreatePath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty(
            "/api/v1/share-invitation-claims",
            out var directClaimPath));
        Assert.True(directClaimPath.TryGetProperty("post", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var created = schemas
            .GetProperty("CreatedGiftCardShareResult")
            .GetProperty("properties");
        Assert.True(created.TryGetProperty("claimUrl", out _));
        Assert.True(created.TryGetProperty("pin", out _));
        var persistedView = schemas
            .GetProperty("GiftCardShareResult")
            .GetProperty("properties");
        Assert.False(persistedView.TryGetProperty("claimSecretHash", out _));
        Assert.False(persistedView.TryGetProperty("pinHash", out _));
        Assert.False(persistedView.TryGetProperty("recipientContact", out _));
        var ownedCard = schemas
            .GetProperty("OwnedGiftCardDetail")
            .GetProperty("properties");
        Assert.True(ownedCard.TryGetProperty("reservedBalance", out _));
        Assert.True(ownedCard.TryGetProperty("availableBalance", out _));
    }

    [Fact]
    public async Task Direct_invitation_supports_new_and_existing_recipient_activation_without_exposing_contact()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 250m);
        var source = await IssueAsync(organizationId, 120m);
        var existingRecipientCard = await IssueAsync(organizationId, 10m);
        var senderUserId = await DistributeAndClaimAsync(
            organizationId,
            source.Id,
            $"direct-sender-{Guid.NewGuid():N}@example.com");
        var existingContact = $"direct-existing-{Guid.NewGuid():N}@example.com";
        var existingRecipientUserId = await DistributeAndClaimAsync(
            organizationId,
            existingRecipientCard.Id,
            existingContact);
        using var sender = IdentityClient(senderUserId);

        var newContact = $"direct-new-{Guid.NewGuid():N}@example.com";
        var newInvitation = await CreateDirectShareAsync(sender, source.Id, 25m, newContact);
        Assert.Equal(HttpStatusCode.Created, newInvitation.Response.StatusCode);
        Assert.Equal("no-store", newInvitation.Response.Headers.CacheControl?.ToString());
        Assert.Equal(GiftCardShareKind.DirectInvitation, newInvitation.Result.Share.Kind);
        Assert.Equal(GiftCardShareState.Pending, newInvitation.Result.Share.State);
        Assert.Equal(
            GiftCardShareContactType.Email,
            newInvitation.Result.Share.RecipientContactType);
        Assert.Equal(
            newInvitation.Result.MaskedRecipientContact,
            newInvitation.Result.Share.MaskedRecipientContact);
        Assert.DoesNotContain(newContact, await newInvitation.Response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var newDelivery = await sender.GetFromJsonAsync<DevelopmentDirectGiftCardShareDeliveryResult>(
            $"/api/v1/me/shares/{newInvitation.Result.Share.Id}/development-delivery",
            JsonOptions);
        Assert.NotNull(newDelivery);
        Assert.DoesNotContain(newContact, JsonSerializer.Serialize(newDelivery, JsonOptions),
            StringComparison.OrdinalIgnoreCase);
        var newClaimToken = ExtractToken(newDelivery.ClaimUrl);
        const int firstEncodedSecretCharacter = 33;
        var invalidClaimToken = newClaimToken[..firstEncodedSecretCharacter] +
            (newClaimToken[firstEncodedSecretCharacter] == 'A' ? "B" : "A") +
            newClaimToken[(firstEncodedSecretCharacter + 1)..];
        var invalidClaim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/share-invitation-claims",
            new
            {
                claimToken = invalidClaimToken,
                password = RecipientPassword,
                idempotencyKey = "sharing-direct-invalid-" + Guid.NewGuid().ToString("N"),
            });
        Assert.Equal(HttpStatusCode.Unauthorized, invalidClaim.StatusCode);
        Assert.DoesNotContain(
            newContact,
            await invalidClaim.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var newClaimKey = "sharing-direct-new-claim-" + Guid.NewGuid().ToString("N");
        var concurrentClaims = await Task.WhenAll(
            fixture.Factory.CreateClient().PostAsJsonAsync(
                "/api/v1/share-invitation-claims",
                new
                {
                    claimToken = newClaimToken,
                    password = RecipientPassword,
                    idempotencyKey = newClaimKey,
                }),
            fixture.Factory.CreateClient().PostAsJsonAsync(
                "/api/v1/share-invitation-claims",
                new
                {
                    claimToken = newClaimToken,
                    password = RecipientPassword,
                    idempotencyKey = newClaimKey,
                }));
        Assert.Equal(1, concurrentClaims.Count(response => response.IsSuccessStatusCode));
        Assert.Equal(1, concurrentClaims.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        var newClaim = (await concurrentClaims.Single(response => response.IsSuccessStatusCode)
            .Content.ReadFromJsonAsync<ClaimedDirectGiftCardShareResult>(JsonOptions))!;
        var retry = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/share-invitation-claims",
            new
            {
                claimToken = newClaimToken,
                password = RecipientPassword,
                idempotencyKey = newClaimKey,
            });
        retry.EnsureSuccessStatusCode();
        var retriedClaim = (await retry.Content
            .ReadFromJsonAsync<ClaimedDirectGiftCardShareResult>(JsonOptions))!;
        Assert.Equal(newClaim.ChildGiftCard.Id, retriedClaim.ChildGiftCard.Id);
        Assert.Equal(newClaim.Share.LedgerTransactionId, retriedClaim.Share.LedgerTransactionId);
        Assert.True(newClaim.IdentityWasCreated);
        Assert.NotNull(newClaim.Session);
        Assert.Equal(source.Id, newClaim.ChildGiftCard.SourceGiftCardId);
        Assert.Equal(25m, newClaim.ChildGiftCard.FundedAmount);

        var existingInvitation = await CreateDirectShareAsync(
            sender,
            source.Id,
            15m,
            existingContact.ToUpperInvariant());
        var existingDelivery = await sender.GetFromJsonAsync<DevelopmentDirectGiftCardShareDeliveryResult>(
            $"/api/v1/me/shares/{existingInvitation.Result.Share.Id}/development-delivery",
            JsonOptions);
        Assert.NotNull(existingDelivery);
        var existingClaimResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/share-invitation-claims",
            new
            {
                claimToken = ExtractToken(existingDelivery.ClaimUrl),
                password = (string?)null,
                idempotencyKey = "sharing-direct-existing-" + Guid.NewGuid().ToString("N"),
            });
        existingClaimResponse.EnsureSuccessStatusCode();
        var existingClaim = (await existingClaimResponse.Content
            .ReadFromJsonAsync<ClaimedDirectGiftCardShareResult>(JsonOptions))!;
        Assert.False(existingClaim.IdentityWasCreated);
        Assert.Null(existingClaim.Session);
        Assert.Equal(existingRecipientUserId, existingClaim.OwnerUserId);
        Assert.Equal(existingRecipientUserId, existingClaim.ChildGiftCard.OwnerUserId);

        var afterClaims = await sender.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{source.Id}",
            JsonOptions);
        Assert.NotNull(afterClaims);
        Assert.Equal(80m, afterClaims.Balance);
        Assert.Equal(0m, afterClaims.ReservedBalance);
        Assert.Equal(80m, afterClaims.AvailableBalance);

        var cancellationKey = "sharing-direct-cancel-create-" + Guid.NewGuid().ToString("N");
        var cancellationContact = $"direct-cancel-{Guid.NewGuid():N}@example.com";
        var cancellable = await CreateDirectShareAsync(
            sender,
            source.Id,
            5m,
            cancellationContact,
            cancellationKey);
        var idempotentCreate = await CreateDirectShareAsync(
            sender,
            source.Id,
            5m,
            cancellationContact,
            cancellationKey);
        Assert.Equal(HttpStatusCode.Created, idempotentCreate.Response.StatusCode);
        Assert.Equal(cancellable.Result.Share.Id, idempotentCreate.Result.Share.Id);
        Assert.True(cancellable.Result.DeliveryDispatchedThisRequest);
        Assert.False(idempotentCreate.Result.DeliveryDispatchedThisRequest);

        var cancelledDelivery = await sender
            .GetFromJsonAsync<DevelopmentDirectGiftCardShareDeliveryResult>(
                $"/api/v1/me/shares/{cancellable.Result.Share.Id}/development-delivery",
                JsonOptions);
        Assert.NotNull(cancelledDelivery);
        var cancelResponse = await sender.PostAsJsonAsync(
            $"/api/v1/me/shares/{cancellable.Result.Share.Id}/cancel",
            new { idempotencyKey = "sharing-direct-cancel-" + Guid.NewGuid().ToString("N") });
        cancelResponse.EnsureSuccessStatusCode();
        var cancelledClaim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/share-invitation-claims",
            new
            {
                claimToken = ExtractToken(cancelledDelivery.ClaimUrl),
                password = RecipientPassword,
                idempotencyKey = "sharing-direct-cancelled-claim-" + Guid.NewGuid().ToString("N"),
            });
        Assert.Equal(HttpStatusCode.Conflict, cancelledClaim.StatusCode);

        await using var platform = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        Assert.Equal(
            2,
            await platform.ScalarCountAsync(
                """
                select count(*) from sharing.shares
                where id in (@new_share, @existing_share)
                  and kind = 'DirectInvitation'
                  and pin_hash is null
                  and length(claim_secret_hash) = 64
                  and recipient_contact is not null
                  and masked_recipient_contact is not null
                """,
                command =>
                {
                    command.Parameters.AddWithValue("new_share", newInvitation.Result.Share.Id);
                    command.Parameters.AddWithValue("existing_share", existingInvitation.Result.Share.Id);
                }));
        Assert.Equal(
            0,
            await platform.ScalarCountAsync(
                """
                select count(*) from audit.audit_records
                where entity_id in (@new_share, @existing_share)
                  and (metadata::text ilike @new_contact or metadata::text ilike @existing_contact)
                """,
                command =>
                {
                    command.Parameters.AddWithValue("new_share", newInvitation.Result.Share.Id.ToString());
                    command.Parameters.AddWithValue("existing_share", existingInvitation.Result.Share.Id.ToString());
                    command.Parameters.AddWithValue("new_contact", $"%{newContact}%");
                    command.Parameters.AddWithValue("existing_contact", $"%{existingContact}%");
                }));

        await using var stranger = await ScopedSqlSession.OpenAsIdentityAsync(
            fixture,
            Guid.CreateVersion7());
        Assert.Equal(
            0,
            await stranger.ScalarCountAsync(
                "select count(*) from sharing.shares where id = @share",
                command => command.Parameters.AddWithValue(
                    "share",
                    newInvitation.Result.Share.Id)));
    }

    [Fact]
    public async Task Protected_share_claim_posts_once_and_terminal_paths_release_reservations()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await AllocateAsync(organizationId, 250m);
        var source = await IssueAsync(organizationId, 100m);
        var recipientCard = await IssueAsync(organizationId, 10m);
        var senderUserId = await DistributeAndClaimAsync(
            organizationId,
            source.Id,
            $"sharing-sender-{Guid.NewGuid():N}@example.com");
        var recipientUserId = await DistributeAndClaimAsync(
            organizationId,
            recipientCard.Id,
            $"sharing-recipient-{Guid.NewGuid():N}@example.com");
        using var sender = IdentityClient(senderUserId);
        using var recipient = IdentityClient(recipientUserId);

        var concurrentRequests = await Task.WhenAll(
            sender.PostAsJsonAsync(
                $"/api/v1/me/gift-cards/{source.Id}/shares",
                new
                {
                    amount = 80m,
                    idempotencyKey = "sharing-concurrent-a-" + Guid.NewGuid().ToString("N"),
                }),
            sender.PostAsJsonAsync(
                $"/api/v1/me/gift-cards/{source.Id}/shares",
                new
                {
                    amount = 80m,
                    idempotencyKey = "sharing-concurrent-b-" + Guid.NewGuid().ToString("N"),
                }));
        Assert.Equal(1, concurrentRequests.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, concurrentRequests.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        var concurrentWinner = (await concurrentRequests
            .Single(response => response.StatusCode == HttpStatusCode.Created)
            .Content.ReadFromJsonAsync<CreatedGiftCardShareResult>(JsonOptions))!;
        var releaseConcurrentWinner = await sender.PostAsJsonAsync(
            $"/api/v1/me/shares/{concurrentWinner.Share.Id}/cancel",
            new { idempotencyKey = "sharing-concurrent-release-" + Guid.NewGuid().ToString("N") });
        releaseConcurrentWinner.EnsureSuccessStatusCode();

        var create = await CreateShareAsync(sender, source.Id, 25m);
        Assert.Equal(HttpStatusCode.Created, create.Response.StatusCode);
        Assert.Equal("no-store", create.Response.Headers.CacheControl?.ToString());
        Assert.Matches("^[0-9]{6}$", create.Result.Pin);
        Assert.Equal(GiftCardShareState.Pending, create.Result.Share.State);

        var reserved = await sender.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{source.Id}",
            JsonOptions);
        Assert.NotNull(reserved);
        Assert.Equal(100m, reserved.Balance);
        Assert.Equal(25m, reserved.ReservedBalance);
        Assert.Equal(75m, reserved.AvailableBalance);

        var claimToken = ExtractToken(create.Result.ClaimUrl);
        var claimKey = "sharing-claim-" + Guid.NewGuid().ToString("N");
        var claimResponse = await recipient.PostAsJsonAsync(
            "/api/v1/share-claims",
            new { claimToken, pin = create.Result.Pin, idempotencyKey = claimKey });
        claimResponse.EnsureSuccessStatusCode();
        Assert.Equal("no-store", claimResponse.Headers.CacheControl?.ToString());
        var claimed = (await claimResponse.Content.ReadFromJsonAsync<ClaimedGiftCardShareResult>(
            JsonOptions))!;
        Assert.Equal(GiftCardShareState.Claimed, claimed.Share.State);
        Assert.Equal(source.Id, claimed.ChildGiftCard.SourceGiftCardId);
        Assert.Equal(source.RootGiftCardId, claimed.ChildGiftCard.RootGiftCardId);
        Assert.Equal(source.Generation + 1, claimed.ChildGiftCard.Generation);
        Assert.Equal(recipientUserId, claimed.ChildGiftCard.OwnerUserId);
        Assert.Equal(25m, claimed.ChildGiftCard.FundedAmount);

        var replay = await recipient.PostAsJsonAsync(
            "/api/v1/share-claims",
            new { claimToken, pin = create.Result.Pin, idempotencyKey = claimKey });
        replay.EnsureSuccessStatusCode();
        var replayed = (await replay.Content.ReadFromJsonAsync<ClaimedGiftCardShareResult>(
            JsonOptions))!;
        Assert.Equal(claimed.ChildGiftCard.Id, replayed.ChildGiftCard.Id);
        Assert.Equal(claimed.Share.LedgerTransactionId, replayed.Share.LedgerTransactionId);

        var afterClaim = await sender.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{source.Id}",
            JsonOptions);
        Assert.NotNull(afterClaim);
        Assert.Equal(75m, afterClaim.Balance);
        Assert.Equal(0m, afterClaim.ReservedBalance);
        Assert.Equal(75m, afterClaim.AvailableBalance);

        var received = await recipient.GetFromJsonAsync<GiftCardSharePage>(
            "/api/v1/me/shares",
            JsonOptions);
        Assert.NotNull(received);
        Assert.Contains(received.Items, item => item.Id == claimed.Share.Id);

        var cancellable = await CreateShareAsync(sender, source.Id, 10m);
        var cancelledResponse = await sender.PostAsJsonAsync(
            $"/api/v1/me/shares/{cancellable.Result.Share.Id}/cancel",
            new { idempotencyKey = "sharing-cancel-" + Guid.NewGuid().ToString("N") });
        cancelledResponse.EnsureSuccessStatusCode();
        var cancelled = (await cancelledResponse.Content.ReadFromJsonAsync<GiftCardShareResult>(
            JsonOptions))!;
        Assert.Equal(GiftCardShareState.Cancelled, cancelled.State);

        var lockable = await CreateShareAsync(sender, source.Id, 5m);
        var wrongPin = lockable.Result.Pin == "000000" ? "111111" : "000000";
        var lockToken = ExtractToken(lockable.Result.ClaimUrl);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await recipient.PostAsJsonAsync(
                "/api/v1/share-claims",
                new
                {
                    claimToken = lockToken,
                    pin = wrongPin,
                    idempotencyKey = $"sharing-wrong-{attempt}-{Guid.NewGuid():N}",
                });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var afterReleases = await sender.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{source.Id}",
            JsonOptions);
        Assert.NotNull(afterReleases);
        Assert.Equal(75m, afterReleases.Balance);
        Assert.Equal(0m, afterReleases.ReservedBalance);
        Assert.Equal(75m, afterReleases.AvailableBalance);

        var lifecycleShare = await CreateShareAsync(sender, source.Id, 7m);
        var directLifecycleShare = await CreateDirectShareAsync(
            sender,
            source.Id,
            6m,
            $"sharing-lifecycle-{Guid.NewGuid():N}@example.com");
        var sourceCancellation = await PlatformOperator(
                fixture,
                PlatformPermissions.GiftCardsManageLifecycle)
            .PostAsJsonAsync(
            $"/api/v1/platform/gift-cards/{source.Id}/lifecycle/cancel",
            new
            {
                reason = "Recipient requested cancellation.",
                idempotencyKey = "sharing-source-cancel-" + Guid.NewGuid().ToString("N"),
            });
        sourceCancellation.EnsureSuccessStatusCode();
        var sentAfterCancellation = await sender.GetFromJsonAsync<GiftCardSharePage>(
            "/api/v1/me/shares",
            JsonOptions);
        Assert.NotNull(sentAfterCancellation);
        Assert.Equal(
            GiftCardShareState.Cancelled,
            sentAfterCancellation.Items.Single(item => item.Id == lifecycleShare.Result.Share.Id).State);
        Assert.Equal(
            GiftCardShareState.Cancelled,
            sentAfterCancellation.Items.Single(item => item.Id == directLifecycleShare.Result.Share.Id).State);
        var cancelledSource = await sender.GetFromJsonAsync<OwnedGiftCardDetail>(
            $"/api/v1/me/gift-cards/{source.Id}",
            JsonOptions);
        Assert.NotNull(cancelledSource);
        Assert.Equal("Cancelled", cancelledSource.LifecycleState);
        Assert.Equal(0m, cancelledSource.Balance);
        Assert.Equal(0m, cancelledSource.ReservedBalance);

        await AssertPersistedSecurityAndAccountingAsync(
            claimed,
            create.Result,
            lockable.Result,
            recipientUserId);
    }

    private async Task AssertPersistedSecurityAndAccountingAsync(
        ClaimedGiftCardShareResult claimed,
        CreatedGiftCardShareResult created,
        CreatedGiftCardShareResult locked,
        Guid recipientUserId)
    {
        await using (var platform = await ScopedSqlSession.OpenAsPlatformAsync(fixture))
        {
            Assert.Equal(
                1,
                await platform.ScalarCountAsync(
                    """
                    select count(*) from ledger.transactions
                    where operation_type = 'gift_card.share_transfer' and id = @transaction
                    """,
                    command => command.Parameters.AddWithValue(
                        "transaction",
                        claimed.Share.LedgerTransactionId!.Value)));
            Assert.Equal(
                2,
                await platform.ScalarCountAsync(
                    "select count(*) from ledger.entries where transaction_id = @transaction",
                    command => command.Parameters.AddWithValue(
                        "transaction",
                        claimed.Share.LedgerTransactionId!.Value)));
            Assert.Equal(
                1,
                await platform.ScalarCountAsync(
                    """
                    select count(*) from sharing.shares
                    where id = @share
                      and claim_secret_hash <> @token
                      and pin_hash <> @pin
                      and length(claim_secret_hash) = 64
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue("share", created.Share.Id);
                        command.Parameters.AddWithValue("token", ExtractToken(created.ClaimUrl));
                        command.Parameters.AddWithValue("pin", created.Pin);
                    }));
            Assert.Equal(
                1,
                await platform.ScalarCountAsync(
                    """
                    select count(*) from sharing.shares
                    where id = @share and state = 'Locked' and failed_pin_attempts = 5
                    """,
                    command => command.Parameters.AddWithValue("share", locked.Share.Id)));
            Assert.Equal(
                1,
                await platform.ScalarCountAsync(
                    """
                    select count(*) from audit.audit_records
                    where operation = 'gift_card.share.claimed' and entity_id = @share
                    """,
                    command => command.Parameters.AddWithValue(
                        "share",
                        claimed.Share.Id.ToString())));

            await using var mutation = platform.Command(
                "update sharing.events set occurred_at_utc = occurred_at_utc where share_id = @share");
            mutation.Parameters.AddWithValue("share", claimed.Share.Id);
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => mutation.ExecuteNonQueryAsync());
            Assert.Equal("55000", exception.SqlState);
        }

        await using var stranger = await ScopedSqlSession.OpenAsIdentityAsync(
            fixture,
            Guid.CreateVersion7());
        Assert.Equal(
            0,
            await stranger.ScalarCountAsync(
                "select count(*) from sharing.shares where id = @share",
                command => command.Parameters.AddWithValue("share", claimed.Share.Id)));

        await using var recipient = await ScopedSqlSession.OpenAsIdentityAsync(fixture, recipientUserId);
        Assert.Equal(
            1,
            await recipient.ScalarCountAsync(
                "select count(*) from sharing.shares where id = @share",
                command => command.Parameters.AddWithValue("share", claimed.Share.Id)));
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
                    businessReference = "SHARING-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "sharing-fund-" + Guid.NewGuid().ToString("N"),
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
                    businessReference = "SHARING-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "sharing-card-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GiftCardResult>(JsonOptions))!;
    }

    private async Task<Guid> DistributeAndClaimAsync(
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
                businessReference = "SHARING-DIST-" + Guid.NewGuid().ToString("N"),
                idempotencyKey = "sharing-dist-" + Guid.NewGuid().ToString("N"),
            });
        distribution.EnsureSuccessStatusCode();
        var invitation = (await distribution.Content.ReadFromJsonAsync<InvitationResponse>())!;
        var delivery = await distributor.GetFromJsonAsync<DeliveryResponse>(
            $"/api/v1/development/organizations/{organizationId}/" +
            $"claim-deliveries/{invitation.Id}");
        Assert.NotNull(delivery);

        var claim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken = ExtractToken(delivery.ClaimUrl),
                password = RecipientPassword,
                idempotencyKey = "sharing-initial-claim-" + Guid.NewGuid().ToString("N"),
            });
        claim.EnsureSuccessStatusCode();
        return (await claim.Content.ReadFromJsonAsync<InitialClaimResponse>())!.OwnerUserId;
    }

    private static async Task<CreatedShareHttpResult> CreateShareAsync(
        HttpClient sender,
        Guid sourceGiftCardId,
        decimal amount)
    {
        var response = await sender.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{sourceGiftCardId}/shares",
            new
            {
                amount,
                idempotencyKey = "sharing-create-" + Guid.NewGuid().ToString("N"),
            });
        var result = (await response.Content.ReadFromJsonAsync<CreatedGiftCardShareResult>(
            JsonOptions))!;
        return new CreatedShareHttpResult(response, result);
    }

    private static async Task<CreatedDirectShareHttpResult> CreateDirectShareAsync(
        HttpClient sender,
        Guid sourceGiftCardId,
        decimal amount,
        string recipientContact,
        string? idempotencyKey = null)
    {
        var response = await sender.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{sourceGiftCardId}/share-invitations",
            new
            {
                amount,
                contactType = "Email",
                recipientContact,
                idempotencyKey = idempotencyKey ??
                    "sharing-direct-create-" + Guid.NewGuid().ToString("N"),
            });
        var result = (await response.Content.ReadFromJsonAsync<CreatedDirectGiftCardShareResult>(
            JsonOptions))!;
        return new CreatedDirectShareHttpResult(response, result);
    }

    private HttpClient IdentityClient(Guid userId)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.CreateAccessToken(userId));
        return client;
    }

    private static string ExtractToken(string claimUrl)
    {
        const string marker = "token=";
        var tokenIndex = claimUrl.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(tokenIndex >= 0);
        return Uri.UnescapeDataString(claimUrl[(tokenIndex + marker.Length)..]);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record InvitationResponse(Guid Id);

    private sealed record DeliveryResponse(string ClaimUrl);

    private sealed record InitialClaimResponse(Guid OwnerUserId);

    private sealed record CreatedShareHttpResult(
        HttpResponseMessage Response,
        CreatedGiftCardShareResult Result);

    private sealed record CreatedDirectShareHttpResult(
        HttpResponseMessage Response,
        CreatedDirectGiftCardShareResult Result);
}
