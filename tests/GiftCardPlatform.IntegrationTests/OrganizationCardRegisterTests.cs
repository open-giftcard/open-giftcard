using System.Net;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// The organization card register (IMPL-033, ADR-052).
///
/// Two things are being proven here, and the second matters more than the
/// first. The register must show a card the company funded after that card has
/// been distributed and claimed, which inventory never could. And it must do so
/// without disclosing what the recipient has since spent.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class OrganizationCardRegisterTests(PlatformApiFixture fixture)
{
    private const string RecipientPassword = "register recipient passphrase";

    private static string RegisterRoute(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId}/reports/card-register";

    [Fact]
    public async Task Register_shows_a_claimed_card_that_inventory_no_longer_lists()
    {
        var organizationId = await CreateFundedOrganizationAsync(500m);
        var inventoryCard = await IssueAsync(organizationId, 100m);
        var claimedCard = await IssueAsync(organizationId, 150m);
        await DistributeAndClaimAsync(organizationId, claimedCard.Id);

        var viewer = RegisterClient(organizationId);

        var inventory = await viewer.GetFromJsonAsync<InventoryPage>(
            $"/api/v1/organizations/{organizationId}/gift-cards/inventory");
        Assert.NotNull(inventory);
        Assert.Contains(inventory.Items, item => item.Id == inventoryCard.Id);
        Assert.DoesNotContain(inventory.Items, item => item.Id == claimedCard.Id);

        var register = await viewer.GetFromJsonAsync<OrganizationCardRegisterPage>(
            RegisterRoute(organizationId));
        Assert.NotNull(register);
        Assert.Contains(register.Items, item => item.GiftCardId == inventoryCard.Id);
        Assert.Contains(register.Items, item => item.GiftCardId == claimedCard.Id);
    }

    /// <summary>
    /// The privacy boundary. A card the company still owns reports its balance,
    /// because that is the company's own money; a card an employee owns does
    /// not, because the balance is a running record of their spending.
    /// </summary>
    [Fact]
    public async Task Balance_is_reported_for_an_owned_card_and_withheld_once_claimed()
    {
        var organizationId = await CreateFundedOrganizationAsync(500m);
        var inventoryCard = await IssueAsync(organizationId, 100m);
        var claimedCard = await IssueAsync(organizationId, 150m);
        await DistributeAndClaimAsync(organizationId, claimedCard.Id);

        var register = await RegisterClient(organizationId)
            .GetFromJsonAsync<OrganizationCardRegisterPage>(
                RegisterRoute(organizationId));
        Assert.NotNull(register);

        var inInventory = Assert.Single(
            register.Items,
            item => item.GiftCardId == inventoryCard.Id);
        Assert.Equal("OrganizationInventory", inInventory.OwnershipState);
        Assert.Equal(100m, inInventory.FundedAmount);
        Assert.Equal(100m, inInventory.RemainingBalance);

        var claimed = Assert.Single(
            register.Items,
            item => item.GiftCardId == claimedCard.Id);
        Assert.Equal("IdentityOwned", claimed.OwnershipState);
        Assert.Equal(150m, claimed.FundedAmount);
        Assert.Null(claimed.RemainingBalance);
    }

    [Fact]
    public async Task Recipient_contact_is_masked_and_the_raw_address_never_appears()
    {
        var organizationId = await CreateFundedOrganizationAsync(300m);
        var card = await IssueAsync(organizationId, 120m);
        var contact = $"register-{Guid.NewGuid():N}@example.com";
        await DistributeAndClaimAsync(organizationId, card.Id, contact);

        var response = await RegisterClient(organizationId)
            .GetAsync(RegisterRoute(organizationId));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(contact, body, StringComparison.OrdinalIgnoreCase);

        var register = await response.Content
            .ReadFromJsonAsync<OrganizationCardRegisterPage>();
        var item = Assert.Single(
            register!.Items,
            row => row.GiftCardId == card.Id);
        Assert.False(string.IsNullOrWhiteSpace(item.MaskedRecipientContact));
        Assert.Contains('*', item.MaskedRecipientContact!);
    }

    [Fact]
    public async Task Caller_without_gift_card_view_is_refused()
    {
        var organizationId = await CreateFundedOrganizationAsync(200m);
        await IssueAsync(organizationId, 50m);

        // A distinct actor, because the default one per organization
        // accumulates every permission any earlier call asked for, and issuing
        // a card above already granted it gift-card view. This user holds a
        // real membership and a real permission, just not this one.
        var response = await OrganizationMember(
                fixture,
                Guid.CreateVersion7(),
                organizationId,
                OrganizationPermissions.MembershipsView)
            .GetAsync(RegisterRoute(organizationId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_this_organizations_register()
    {
        var organizationId = await CreateFundedOrganizationAsync(200m);
        var card = await IssueAsync(organizationId, 75m);

        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var intruder = OrganizationMember(
            fixture,
            otherOrganizationId,
            OrganizationPermissions.GiftCardsView);

        var response = await intruder.GetAsync(RegisterRoute(organizationId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // And their own register does not leak the other tenant's card either.
        var own = await intruder.GetFromJsonAsync<OrganizationCardRegisterPage>(
            RegisterRoute(otherOrganizationId));
        Assert.NotNull(own);
        Assert.DoesNotContain(own.Items, item => item.GiftCardId == card.Id);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        var organizationId = await CreateFundedOrganizationAsync(100m);

        var response = await fixture.Factory.CreateClient()
            .GetAsync(RegisterRoute(organizationId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ownership_filter_narrows_the_register()
    {
        var organizationId = await CreateFundedOrganizationAsync(500m);
        var inventoryCard = await IssueAsync(organizationId, 60m);
        var claimedCard = await IssueAsync(organizationId, 70m);
        await DistributeAndClaimAsync(organizationId, claimedCard.Id);

        var page = await RegisterClient(organizationId)
            .GetFromJsonAsync<OrganizationCardRegisterPage>(
                RegisterRoute(organizationId) + "?ownershipState=IdentityOwned");

        Assert.NotNull(page);
        Assert.Contains(page.Items, item => item.GiftCardId == claimedCard.Id);
        Assert.DoesNotContain(page.Items, item => item.GiftCardId == inventoryCard.Id);
        Assert.All(page.Items, item => Assert.Equal("IdentityOwned", item.OwnershipState));
    }

    [Fact]
    public async Task Reference_filter_matches_literally_and_is_not_a_pattern()
    {
        var organizationId = await CreateFundedOrganizationAsync(300m);
        var card = await IssueAsync(organizationId, 80m);

        var viewer = RegisterClient(organizationId);

        var found = await viewer.GetFromJsonAsync<OrganizationCardRegisterPage>(
            RegisterRoute(organizationId) + $"?reference={card.PublicReference}");
        Assert.NotNull(found);
        Assert.Equal(card.Id, Assert.Single(found.Items).GiftCardId);

        // A wildcard is a character to match, not a pattern to expand.
        var wildcard = await viewer.GetFromJsonAsync<OrganizationCardRegisterPage>(
            RegisterRoute(organizationId) + "?reference=%25");
        Assert.NotNull(wildcard);
        Assert.Empty(wildcard.Items);
    }

    [Fact]
    public async Task Unknown_filter_values_are_refused_rather_than_ignored()
    {
        var organizationId = await CreateFundedOrganizationAsync(100m);
        var viewer = RegisterClient(organizationId);

        var lifecycle = await viewer.GetAsync(
            RegisterRoute(organizationId) + "?lifecycleState=Revoked");
        Assert.Equal(HttpStatusCode.BadRequest, lifecycle.StatusCode);

        var ownership = await viewer.GetAsync(
            RegisterRoute(organizationId) + "?ownershipState=PlatformOwned");
        Assert.Equal(HttpStatusCode.BadRequest, ownership.StatusCode);
    }

    [Fact]
    public async Task Pages_are_stable_and_a_cursor_is_bound_to_its_filters()
    {
        var organizationId = await CreateFundedOrganizationAsync(500m);
        for (var i = 0; i < 3; i++)
        {
            await IssueAsync(organizationId, 10m + i);
        }

        var viewer = RegisterClient(organizationId);
        var first = await viewer.GetFromJsonAsync<OrganizationCardRegisterPage>(
            RegisterRoute(organizationId) + "?limit=2");
        Assert.NotNull(first);
        Assert.Equal(2, first.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));

        var second = await viewer.GetFromJsonAsync<OrganizationCardRegisterPage>(
            RegisterRoute(organizationId) +
            $"?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.NotNull(second);
        Assert.NotEmpty(second.Items);

        var firstIds = first.Items.Select(item => item.GiftCardId).ToHashSet();
        Assert.All(
            second.Items,
            item => Assert.DoesNotContain(item.GiftCardId, firstIds));

        // Reusing the cursor under a different filter set must not continue
        // silently against a different result set.
        var mismatched = await viewer.GetAsync(
            RegisterRoute(organizationId) +
            $"?limit=2&ownershipState=IdentityOwned" +
            $"&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
    }

    private HttpClient RegisterClient(Guid organizationId) =>
        OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsView);

    private async Task<Guid> CreateFundedOrganizationAsync(decimal amount)
    {
        var organizationId = await CreateOrganizationAsync(fixture);
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
                    businessReference = "REG-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "reg-fund-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return organizationId;
    }

    private async Task<CardResponse> IssueAsync(Guid organizationId, decimal amount)
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
                    businessReference = "REG-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "reg-card-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CardResponse>())!;
    }

    private async Task DistributeAndClaimAsync(
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
                    $"register-{Guid.NewGuid():N}@example.com",
                businessReference = "REG-DIST-" + Guid.NewGuid().ToString("N"),
                idempotencyKey = "reg-dist-" + Guid.NewGuid().ToString("N"),
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
                idempotencyKey = "reg-claim-" + Guid.NewGuid().ToString("N"),
            });
        claim.EnsureSuccessStatusCode();
    }

    private sealed record CardResponse(Guid Id, string PublicReference);

    private sealed record InvitationResponse(Guid Id);

    private sealed record DeliveryResponse(string ClaimUrl);

    private sealed record InventoryPage(IReadOnlyList<InventoryItem> Items);

    private sealed record InventoryItem(Guid Id);
}
