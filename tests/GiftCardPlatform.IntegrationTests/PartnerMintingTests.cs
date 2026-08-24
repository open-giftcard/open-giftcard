using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.AspNetCore.Hosting;
using static GiftCardPlatform.IntegrationTests.AuthorizationTestSupport;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class PartnerMintingTests(PlatformApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Mint_debits_only_the_partner_float_and_records_machine_attribution()
    {
        var (partner, client) = await RegisterPartnerAndClientAsync();
        await FundAsync(partner.RootOrganizationId, 100m);
        using var reseller = await AuthenticatedClientAsync(client);
        var request = NewRequest(40m);

        var first = await reseller.PostAsJsonAsync(MintRoute, request);
        var retry = await reseller.PostAsJsonAsync(MintRoute, request);

        first.EnsureSuccessStatusCode();
        retry.EnsureSuccessStatusCode();
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        var minted = (await first.Content.ReadFromJsonAsync<EpinResponse>())!;
        var retried = (await retry.Content.ReadFromJsonAsync<EpinResponse>())!;
        Assert.Equal(minted, retried);
        var card = minted.GiftCard;
        Assert.Equal(partner.RootOrganizationId, card.FundingOrganizationId);
        Assert.Equal(partner.RootOrganizationId, card.IssuingOrganizationId);
        Assert.Null(card.OwnerOrganizationId);
        Assert.Equal("AwaitingClaim", card.OwnershipState);
        Assert.Equal(40m, card.FundedAmount);
        Assert.Equal(client.Client.Id, card.IssuedByUserId);
        Assert.Null(card.IssuedByMembershipId);
        Assert.Equal(client.Client.Id, card.IssuedByPartnerClientId);
        Assert.Matches("^[0-9]{6}$", minted.Pin);
        Assert.Contains($"token={minted.InvitationId:N}", minted.ClaimUrl, StringComparison.Ordinal);

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            partner.RootOrganizationId);
        Assert.Equal(60m, await CorporateBalanceAsync(session, partner.RootOrganizationId));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where operation = 'gift_card.issued'
                  and entity_id = @card_id
                  and actor_type = 'PartnerClient'
                  and actor_user_id = @client_id
                  and actor_membership_id is null
                """,
                command =>
                {
                    command.Parameters.AddWithValue("card_id", card.Id.ToString());
                    command.Parameters.AddWithValue("client_id", client.Client.Id);
                }));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.invitations
                where id = @invitation_id
                  and kind = 'OrphanPin'
                  and recipient_contact is null
                  and pin_hash is not null
                  and pin_hash <> @raw_pin
                  and distributed_by_partner_client_id = @client_id
                """,
                command =>
                {
                    command.Parameters.AddWithValue("invitation_id", minted.InvitationId);
                    command.Parameters.AddWithValue("raw_pin", minted.Pin);
                    command.Parameters.AddWithValue("client_id", client.Client.Id);
                }));
    }

    [Fact]
    public async Task Mint_quota_is_shared_across_api_instances_and_is_rls_isolated()
    {
        var (partner, registeredClient) = await RegisterPartnerAndClientAsync();
        await FundAsync(partner.RootOrganizationId, 30m);
        using var authenticated = await AuthenticatedClientAsync(registeredClient);
        var authorization = authenticated.DefaultRequestHeaders.Authorization;

        await using var firstFactory = fixture.Factory.WithWebHostBuilder(webHost =>
        {
            webHost.UseSetting("Partners:MintRateLimit:PermitLimit", "2");
            webHost.UseSetting("Partners:MintRateLimit:WindowSeconds", "3600");
        });
        await using var secondFactory = fixture.Factory.WithWebHostBuilder(webHost =>
        {
            webHost.UseSetting("Partners:MintRateLimit:PermitLimit", "2");
            webHost.UseSetting("Partners:MintRateLimit:WindowSeconds", "3600");
        });
        using var firstInstance = firstFactory.CreateClient();
        using var secondInstance = secondFactory.CreateClient();
        firstInstance.DefaultRequestHeaders.Authorization = authorization;
        secondInstance.DefaultRequestHeaders.Authorization = authorization;

        var first = await firstInstance.PostAsJsonAsync(MintRoute, NewRequest(10m));
        var second = await secondInstance.PostAsJsonAsync(MintRoute, NewRequest(10m));
        var refused = await firstInstance.PostAsJsonAsync(MintRoute, NewRequest(10m));

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = (await refused.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(
            "partner.mint.rate_limit_exceeded",
            problem.GetProperty("code").GetString());

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using (var security = session.Command(
            """
            select c.relrowsecurity,
                   c.relforcerowsecurity,
                   r.rolsuper,
                   r.rolbypassrls
            from pg_class c
            join pg_roles r on r.rolname = current_user
            where c.oid = 'partners.mint_rate_windows'::regclass;
            """))
        await using (var reader = await security.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
        }
        Assert.Equal(
            0,
            await session.ScalarCountAsync(
                "select count(*) from partners.mint_rate_windows;"));
        await using (var setPartner = session.Command(
            "select set_config('app.partner_client_id', @client_id, true);"))
        {
            setPartner.Parameters.AddWithValue(
                "client_id",
                registeredClient.Client.Id.ToString());
            await setPartner.ExecuteScalarAsync();
        }
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                "select count(*) from partners.mint_rate_windows;"));
    }

    [Fact]
    public async Task Buyer_claim_requires_both_token_and_pin_and_can_create_an_identity()
    {
        var (partner, client) = await RegisterPartnerAndClientAsync();
        await FundAsync(partner.RootOrganizationId, 50m);
        using var reseller = await AuthenticatedClientAsync(client);
        var mintResponse = await reseller.PostAsJsonAsync(MintRoute, NewRequest(25m));
        mintResponse.EnsureSuccessStatusCode();
        var minted = (await mintResponse.Content.ReadFromJsonAsync<EpinResponse>())!;
        var claimToken = ExtractClaimToken(minted.ClaimUrl);
        var claimKey = "epin-claim-" + Guid.NewGuid().ToString("N");

        var wrongPin = minted.Pin == "000000" ? "000001" : "000000";
        var refused = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken,
                pin = wrongPin,
                contactType = "Email",
                recipientContact = $"buyer-{Guid.NewGuid():N}@example.com",
                password = "ValidPass!234",
                idempotencyKey = claimKey,
            });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        var claimed = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken,
                pin = minted.Pin,
                contactType = "Email",
                recipientContact = email,
                password = "ValidPass!234",
                idempotencyKey = claimKey,
            });
        claimed.EnsureSuccessStatusCode();
        var result = (await claimed.Content.ReadFromJsonAsync<ClaimResponse>())!;
        Assert.True(result.IdentityWasCreated);
        Assert.NotNull(result.Session);
        Assert.Equal(minted.GiftCard.Id, result.GiftCard.Id);
        Assert.Equal(result.OwnerUserId, result.GiftCard.OwnerUserId);
        Assert.Equal("IdentityOwned", result.GiftCard.OwnershipState);
    }

    [Fact]
    public async Task Mint_is_refused_without_prepaid_float_and_without_partner_authentication()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();
        var request = NewRequest(10m);

        using var reseller = await AuthenticatedClientAsync(client);
        var noFloat = await reseller.PostAsJsonAsync(MintRoute, request);
        var anonymous = await fixture.Factory.CreateClient().PostAsJsonAsync(MintRoute, request);

        Assert.Equal(HttpStatusCode.Conflict, noFloat.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private async Task<(PartnerResult Partner, RegisteredPartnerApiClientResult Client)>
        RegisterPartnerAndClientAsync()
    {
        var organizationResponse = await Operator().PostAsJsonAsync(
            "/api/v1/organizations",
            new
            {
                name = "Mint reseller " + Guid.NewGuid().ToString("N")[..6],
                code = "MINT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            });
        organizationResponse.EnsureSuccessStatusCode();
        var organization = (await organizationResponse.Content.ReadFromJsonAsync<IdResponse>())!;

        var partnerResponse = await Operator().PostAsJsonAsync(
            "/api/v1/partners",
            new
            {
                rootOrganizationId = organization.Id,
                code = "PTR-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
                displayName = "Mint reseller",
            });
        partnerResponse.EnsureSuccessStatusCode();
        var partner = (await partnerResponse.Content
            .ReadFromJsonAsync<PartnerResult>(JsonOptions))!;

        var clientResponse = await Operator().PostAsJsonAsync(
            $"/api/v1/partners/{partner.Id}/clients",
            new
            {
                code = "PTRC-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
                displayName = "Mint key",
            });
        clientResponse.EnsureSuccessStatusCode();
        var client = (await clientResponse.Content
            .ReadFromJsonAsync<RegisteredPartnerApiClientResult>(JsonOptions))!;
        return (partner, client);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(
        RegisteredPartnerApiClientResult client)
    {
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = client.Client.Code, clientSecret = client.Secret });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<PartnerAccessTokenResult>())!;
        var httpClient = fixture.Factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return httpClient;
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
            from ledger.accounts account
            left join ledger.entries entry on entry.account_id = account.id
            where account.organization_id = @organization_id
              and account.type = 'OrganizationCorporateCredit'
              and account.currency = 'TRY'
            """);
        command.Parameters.AddWithValue("organization_id", organizationId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private HttpClient Operator() =>
        PlatformOperator(
            fixture,
            PlatformPermissions.OrganizationsCreate,
            PlatformPermissions.PartnersManage);

    private static MintRequest NewRequest(decimal amount) =>
        new(
            amount,
            "TRY",
            DateTimeOffset.UtcNow.AddYears(1),
            "ORDER-" + Guid.NewGuid().ToString("N"),
            "partner-mint-" + Guid.NewGuid().ToString("N"));

    private const string MintRoute = "/api/v1/partners/gift-cards/mint";

    private static string ExtractClaimToken(string claimUrl)
    {
        var query = new Uri(claimUrl).Query.TrimStart('?');
        var value = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith("token=", StringComparison.Ordinal))
            ["token=".Length..];
        return Uri.UnescapeDataString(value);
    }

    private sealed record MintRequest(
        decimal Amount,
        string Currency,
        DateTimeOffset ExpiresAtUtc,
        string BusinessReference,
        string IdempotencyKey);

    private sealed record CardResponse(
        Guid Id,
        Guid FundingOrganizationId,
        Guid IssuingOrganizationId,
        Guid? OwnerOrganizationId,
        Guid? OwnerUserId,
        string OwnershipState,
        decimal FundedAmount,
        Guid IssuedByUserId,
        Guid? IssuedByMembershipId,
        Guid? IssuedByPartnerClientId);

    private sealed record EpinResponse(
        CardResponse GiftCard,
        Guid InvitationId,
        string ClaimUrl,
        string Pin,
        DateTimeOffset ClaimExpiresAtUtc);

    private sealed record ClaimResponse(
        Guid OwnerUserId,
        bool IdentityWasCreated,
        SessionResponse? Session,
        CardResponse GiftCard);

    private sealed record SessionResponse(string AccessToken);

    private sealed record IdResponse(Guid Id);
}
