using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class GiftCardDistributionTests(PlatformApiFixture fixture)
{
    private const string Password = "recipient horse battery staple";
    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    [Fact]
    public async Task Email_delivery_claims_atomically_without_moving_ledger_value()
    {
        var setup = await PrepareCardAsync();
        var email = UniqueEmail();
        var invitation = await DistributeAsync(setup, RecipientContactType.Email, email);
        var token = await GetClaimTokenAsync(setup.OrganizationId, invitation.Id);

        var claimResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            NewClaim(token));

        claimResponse.EnsureSuccessStatusCode();
        Assert.Contains(
            "no-store",
            claimResponse.Headers.CacheControl?.ToString(),
            StringComparison.OrdinalIgnoreCase);
        var claim = (await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>())!;
        Assert.True(claim.IdentityWasCreated);
        Assert.NotNull(claim.Session);
        Assert.False(string.IsNullOrWhiteSpace(claim.Session.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(claim.Session.RefreshToken));
        Assert.True(
            claim.Session.AccessTokenExpiresAtUtc <
            claim.Session.RefreshTokenExpiresAtUtc);
        Assert.Equal(invitation.Id, claim.InvitationId);
        Assert.Equal("IdentityOwned", claim.GiftCard.OwnershipState);
        Assert.Equal("Active", claim.GiftCard.LifecycleState);
        Assert.Equal(claim.OwnerUserId, claim.GiftCard.OwnerUserId);
        Assert.Null(claim.GiftCard.OwnerOrganizationId);
        Assert.Equal(invitation.Id, claim.GiftCard.DistributionInvitationId);
        var owner = fixture.Factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            claim.Session.AccessToken);
        var ownedCards = await owner.GetAsync("/api/v1/me/gift-cards");
        ownedCards.EnsureSuccessStatusCode();
        Assert.Contains(
            claim.GiftCard.PublicReference,
            await ownedCards.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.invitations
                where id = @id
                  and state = 'Claimed'
                  and claimed_by_user_id = @user_id
                  and claim_secret_hash <> @token
                """,
                command =>
                {
                    command.Parameters.AddWithValue("id", invitation.Id);
                    command.Parameters.AddWithValue("user_id", claim.OwnerUserId);
                    command.Parameters.AddWithValue("token", token);
                }));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.events
                where invitation_id = @id
                """,
                command => command.Parameters.AddWithValue("id", invitation.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from ledger.entries
                where account_id = @account_id
                """,
                command => command.Parameters.AddWithValue(
                    "account_id",
                    setup.Card.LedgerAccountId)));
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                """
                select count(*)
                from audit.audit_records
                where operation in ('gift_card.distributed', 'gift_card.claimed')
                  and metadata->>'giftCardId' = @card_id
                """,
                command => command.Parameters.AddWithValue(
                    "card_id",
                    setup.Card.Id.ToString())));

        await using var global = await fixture.OpenAppConnectionAsync();
        await using var identity = new NpgsqlCommand(
            """
            select count(*)
            from identity.users
            where id = @user_id
              and normalized_email = @email
              and phone_number is null
            """,
            global);
        identity.Parameters.AddWithValue("user_id", claim.OwnerUserId);
        identity.Parameters.AddWithValue("email", email.ToUpperInvariant());
        Assert.Equal(1L, (long)(await identity.ExecuteScalarAsync())!);

        await using var membership = new NpgsqlCommand(
            """
            select count(*)
            from organizations.organization_memberships
            where user_id = @user_id
            """,
            global);
        membership.Parameters.AddWithValue("user_id", claim.OwnerUserId);
        Assert.Equal(0L, (long)(await membership.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Phone_recipient_activates_and_logs_in_without_repeated_delivery()
    {
        var setup = await PrepareCardAsync();
        var phone = "+90555" + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999);
        var invitation = await DistributeAsync(setup, RecipientContactType.Phone, phone);
        var deliveryBefore = await GetDeliveryAsync(setup.OrganizationId, invitation.Id);
        var token = ExtractToken(deliveryBefore.ClaimUrl);

        var claimResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            NewClaim(token));
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>())!;

        var login = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { phoneNumber = phone, password = Password });
        login.EnsureSuccessStatusCode();

        var deliveryAfter = await GetDeliveryAsync(setup.OrganizationId, invitation.Id);
        Assert.Equal(deliveryBefore, deliveryAfter);

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select count(*)
            from identity.users
            where id = @id
              and email is null
              and normalized_phone_number = @phone
            """,
            connection);
        command.Parameters.AddWithValue("id", claim.OwnerUserId);
        command.Parameters.AddWithValue("phone", phone);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Phone_distribution_is_rejected_before_commit_without_an_sms_sender()
    {
        var setup = await PrepareCardAsync();
        var request = NewDistribution(
            RecipientContactType.Phone,
            "+90555" + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999));
        var keysPath = Path.Combine(
            Path.GetTempPath(),
            "open-giftcard-channel-tests",
            Guid.NewGuid().ToString("N"));

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            setup.OrganizationId);
        var queuedBefore = await session.ScalarCountAsync(
            "select count(*) from notifications.outbox_messages;");
        using var authorized = DistributionClient(setup.OrganizationId);
        (await authorized.GetAsync("/health")).EnsureSuccessStatusCode();
        await using var production = fixture.Factory.WithWebHostBuilder(webHost =>
        {
            webHost.UseEnvironment("Production");
            webHost.UseSetting("DataProtection:KeysPath", keysPath);
        });
        using var client = production.CreateClient();
        client.DefaultRequestHeaders.Authorization = authorized.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add(
            OrganizationIdHeader,
            setup.OrganizationId.ToString());

        try
        {
            var response = await client.PostAsJsonAsync(
                DistributionRoute(setup.OrganizationId, setup.Card.Id),
                request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
            Assert.Equal(
                "notification.channel.unconfigured",
                problem.GetProperty("code").GetString());
            Assert.Equal(
                0,
                await session.ScalarCountAsync(
                    """
                    select count(*)
                    from distribution.invitations
                    where idempotency_key = @idempotency_key;
                    """,
                    command => command.Parameters.AddWithValue(
                        "idempotency_key",
                        request.IdempotencyKey)));
            Assert.Equal(
                queuedBefore,
                await session.ScalarCountAsync(
                    "select count(*) from notifications.outbox_messages;"));
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Existing_phone_distribution_replays_after_sms_sender_is_removed()
    {
        var setup = await PrepareCardAsync();
        var request = NewDistribution(
            RecipientContactType.Phone,
            "+90555" + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999));
        using var authorized = DistributionClient(setup.OrganizationId);
        var firstResponse = await authorized.PostAsJsonAsync(
            DistributionRoute(setup.OrganizationId, setup.Card.Id),
            request);
        firstResponse.EnsureSuccessStatusCode();
        var first = (await firstResponse.Content.ReadFromJsonAsync<InvitationResponse>(
            ApiJsonOptions))!;

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            setup.OrganizationId);
        var queuedBefore = await session.ScalarCountAsync(
            "select count(*) from notifications.outbox_messages;");
        var keysPath = Path.Combine(
            Path.GetTempPath(),
            "open-giftcard-channel-tests",
            Guid.NewGuid().ToString("N"));
        await using var production = fixture.Factory.WithWebHostBuilder(webHost =>
        {
            webHost.UseEnvironment("Production");
            webHost.UseSetting("DataProtection:KeysPath", keysPath);
        });
        using var client = production.CreateClient();
        client.DefaultRequestHeaders.Authorization = authorized.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add(
            OrganizationIdHeader,
            setup.OrganizationId.ToString());

        try
        {
            var replayResponse = await client.PostAsJsonAsync(
                DistributionRoute(setup.OrganizationId, setup.Card.Id),
                request);

            replayResponse.EnsureSuccessStatusCode();
            var replay = (await replayResponse.Content.ReadFromJsonAsync<InvitationResponse>(
                ApiJsonOptions))!;
            Assert.Equal(first, replay);
            Assert.Equal(
                queuedBefore,
                await session.ScalarCountAsync(
                    "select count(*) from notifications.outbox_messages;"));
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Existing_contact_is_associated_without_resetting_its_password()
    {
        var setup = await PrepareCardAsync();
        var email = UniqueEmail();
        var existingUserId = await CreateIdentityUserAsync(email, Password);
        var invitation = await DistributeAsync(setup, RecipientContactType.Email, email);
        var token = await GetClaimTokenAsync(setup.OrganizationId, invitation.Id);

        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            new
            {
                claimToken = token,
                password = (string?)null,
                idempotencyKey = "claim-" + Guid.NewGuid().ToString("N"),
            });

        response.EnsureSuccessStatusCode();
        var claim = (await response.Content.ReadFromJsonAsync<ClaimResponse>())!;
        Assert.False(claim.IdentityWasCreated);
        Assert.Null(claim.Session);
        Assert.Equal(existingUserId, claim.OwnerUserId);

        var login = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = Password });
        login.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Distribution_and_claim_are_idempotent_and_changed_intent_conflicts()
    {
        var setup = await PrepareCardAsync();
        var request = NewDistribution(RecipientContactType.Email, UniqueEmail());
        var client = DistributionClient(setup.OrganizationId);

        var firstResponse = await client.PostAsJsonAsync(
            DistributionRoute(setup.OrganizationId, setup.Card.Id),
            request);
        var secondResponse = await client.PostAsJsonAsync(
            DistributionRoute(setup.OrganizationId, setup.Card.Id),
            request);
        firstResponse.EnsureSuccessStatusCode();
        secondResponse.EnsureSuccessStatusCode();
        var first = (await firstResponse.Content.ReadFromJsonAsync<InvitationResponse>(
            ApiJsonOptions))!;
        var second = (await secondResponse.Content.ReadFromJsonAsync<InvitationResponse>(
            ApiJsonOptions))!;
        Assert.Equal(first, second);

        var conflict = await client.PostAsJsonAsync(
            DistributionRoute(setup.OrganizationId, setup.Card.Id),
            request with { RecipientContact = UniqueEmail() });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var token = await GetClaimTokenAsync(setup.OrganizationId, first.Id);
        var claim = NewClaim(token);
        var firstClaim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            claim);
        var secondClaim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            claim);
        firstClaim.EnsureSuccessStatusCode();
        secondClaim.EnsureSuccessStatusCode();
        var firstClaimResult =
            (await firstClaim.Content.ReadFromJsonAsync<ClaimResponse>())!;
        var secondClaimResult =
            (await secondClaim.Content.ReadFromJsonAsync<ClaimResponse>())!;
        Assert.Equal(
            firstClaimResult with { Session = null },
            secondClaimResult with { Session = null });
        Assert.NotNull(firstClaimResult.Session);
        Assert.NotNull(secondClaimResult.Session);
        Assert.NotEqual(
            firstClaimResult.Session.RefreshToken,
            secondClaimResult.Session.RefreshToken);

        var wrongPasswordReplay = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            claim with { Password = "a different recipient passphrase" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordReplay.StatusCode);

        var invalidAfterClaim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            NewClaim(TamperToken(token)));
        Assert.Equal(HttpStatusCode.Unauthorized, invalidAfterClaim.StatusCode);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);
        Assert.Equal(
            2,
            await session.ScalarCountAsync(
                "select count(*) from distribution.events where invitation_id = @id",
                command => command.Parameters.AddWithValue("id", first.Id)));
    }

    [Fact]
    public async Task Distribution_requires_exact_permission_and_an_eligible_owned_card()
    {
        var setup = await PrepareCardAsync();
        var request = NewDistribution(RecipientContactType.Email, UniqueEmail());
        var route = DistributionRoute(setup.OrganizationId, setup.Card.Id);

        var unauthenticated = await fixture.Factory.CreateClient().PostAsJsonAsync(route, request);
        var wrongPermission = await OrganizationMember(
                fixture,
                setup.OrganizationId,
                OrganizationPermissions.GiftCardsView)
            .PostAsJsonAsync(route, request);
        var platform = await PlatformOperator(
                fixture,
                PlatformPermissions.CorporateCreditsAllocate)
            .PostAsJsonAsync(route, request);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);

        var otherTenant = await CreateOrganizationAsync(fixture);
        var crossTenant = await DistributionClient(otherTenant).PostAsJsonAsync(
            DistributionRoute(otherTenant, setup.Card.Id),
            request with { IdempotencyKey = "distribution-" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
    }

    [Fact]
    public async Task Failed_claims_are_bounded_and_lock_the_invitation()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(
            setup,
            RecipientContactType.Phone,
            "+90555" + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999));
        var validToken = await GetClaimTokenAsync(setup.OrganizationId, invitation.Id);
        var tokenPrefix = validToken[..validToken.IndexOf('.', StringComparison.Ordinal)];
        var wrongSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var wrongToken = $"{tokenPrefix}.{wrongSecret}";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
                ClaimRoute,
                NewClaim(wrongToken) with
                {
                    IdempotencyKey = $"failed-claim-{attempt}-{Guid.NewGuid():N}",
                });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var locked = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            NewClaim(validToken));
        var lockedAgain = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            NewClaim(validToken));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lockedAgain.StatusCode);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.invitations
                where id = @id
                  and state = 'Locked'
                  and failed_claim_attempts = 5
                """,
                command => command.Parameters.AddWithValue("id", invitation.Id)));
        Assert.Equal(
            6,
            await session.ScalarCountAsync(
                "select count(*) from distribution.events where invitation_id = @id",
                command => command.Parameters.AddWithValue("id", invitation.Id)));
    }

    [Fact]
    public async Task Claim_endpoint_is_rate_limited()
    {
        await using var limitedFactory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.UseSetting("Distribution:ClaimRateLimit:PermitLimit", "2"));
        var client = limitedFactory.CreateClient();
        var request = new ClaimRequest(
            "invalid-claim-token",
            Password,
            "claim-" + Guid.NewGuid().ToString("N"));

        var first = await client.PostAsJsonAsync(ClaimRoute, request);
        var second = await client.PostAsJsonAsync(ClaimRoute, request);
        var third = await client.PostAsJsonAsync(ClaimRoute, request);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Trusted_forwarded_clients_receive_independent_claim_quotas()
    {
        await using var trustedFactory = fixture.Factory.WithWebHostBuilder(webHost =>
        {
            webHost.UseSetting("Distribution:ClaimRateLimit:PermitLimit", "1");
            webHost.UseSetting(
                "Networking:ForwardedHeaders:KnownProxies:0",
                IPAddress.Loopback.ToString());
            webHost.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new RemoteIpStartupFilter(IPAddress.Loopback)));
        });
        var client = trustedFactory.CreateClient();

        var first = await PostInvalidClaimAsync(client, "203.0.113.10");
        var secondClient = await PostInvalidClaimAsync(client, "203.0.113.11");
        var repeatedFirst = await PostInvalidClaimAsync(client, "203.0.113.10");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondClient.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeatedFirst.StatusCode);
    }

    [Fact]
    public async Task Untrusted_forwarded_header_cannot_evade_claim_quota()
    {
        await using var untrustedFactory = fixture.Factory.WithWebHostBuilder(webHost =>
        {
            webHost.UseSetting("Distribution:ClaimRateLimit:PermitLimit", "1");
            webHost.UseSetting(
                "Networking:ForwardedHeaders:KnownProxies:0",
                "192.0.2.1");
            webHost.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new RemoteIpStartupFilter(IPAddress.Loopback)));
        });
        var client = untrustedFactory.CreateClient();

        var first = await PostInvalidClaimAsync(client, "203.0.113.10");
        var spoofedSecond = await PostInvalidClaimAsync(client, "203.0.113.11");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofedSecond.StatusCode);
    }

    [Fact]
    public async Task Claim_session_failure_rolls_back_new_identity_and_ownership()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(
            setup,
            RecipientContactType.Email,
            UniqueEmail());
        var token = await GetClaimTokenAsync(setup.OrganizationId, invitation.Id);
        var request = NewClaim(token);
        await using var failingFactory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.ConfigureServices(services =>
            {
                services.RemoveAll<IRecipientClaimSessionIssuer>();
                services.AddScoped<IRecipientClaimSessionIssuer, FailingClaimSessionIssuer>();
            }));

        var failed = await failingFactory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            request);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        var retry = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            request);
        retry.EnsureSuccessStatusCode();
        var claim = (await retry.Content.ReadFromJsonAsync<ClaimResponse>())!;
        Assert.True(claim.IdentityWasCreated);
        Assert.NotNull(claim.Session);
    }

    [Fact]
    public async Task Development_OpenAPI_exposes_optional_claim_session()
    {
        var response = await fixture.Factory.CreateClient()
            .GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var claimSchema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("GiftCardClaimApiResponse");
        var properties = claimSchema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("session", out _));
        if (claimSchema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain(
                "session",
                required
                    .EnumerateArray()
                    .Select(item => item.GetString()));
        }
    }

    [Fact]
    public async Task Concurrent_claims_cannot_create_two_owners()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(
            setup,
            RecipientContactType.Email,
            UniqueEmail());
        var token = await GetClaimTokenAsync(setup.OrganizationId, invitation.Id);
        var request = NewClaim(token);

        var attempts = await Task.WhenAll(
            fixture.Factory.CreateClient().PostAsJsonAsync(ClaimRoute, request),
            fixture.Factory.CreateClient().PostAsJsonAsync(ClaimRoute, request));
        Assert.Contains(attempts, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(
            attempts,
            response => Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));

        var retry = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            request);
        retry.EnsureSuccessStatusCode();

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(*)
                from distribution.invitations
                where id = @id
                  and state = 'Claimed'
                  and claimed_by_user_id is not null
                """,
                command => command.Parameters.AddWithValue("id", invitation.Id)));
        Assert.Equal(
            1,
            await session.ScalarCountAsync(
                """
                select count(distinct owner_user_id)
                from gift_cards.gift_cards
                where id = @card_id
                  and owner_user_id is not null
                """,
                command => command.Parameters.AddWithValue("card_id", setup.Card.Id)));
    }

    [Fact]
    public async Task Claimant_and_tenant_rls_are_exact_and_contextless_access_fails_closed()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(
            setup,
            RecipientContactType.Email,
            UniqueEmail());
        var token = await GetClaimTokenAsync(setup.OrganizationId, invitation.Id);
        var claimResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            ClaimRoute,
            NewClaim(token));
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>())!;

        await using var tenant =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);
        await using var owner =
            await ScopedSqlSession.OpenAsIdentityAsync(fixture, claim.OwnerUserId);
        await using var stranger =
            await ScopedSqlSession.OpenAsIdentityAsync(fixture, Guid.CreateVersion7());
        await using var noContext = await fixture.OpenAppConnectionAsync();
        await using var contextlessInvitation = new NpgsqlCommand(
            "select count(*) from distribution.invitations",
            noContext);

        Assert.Equal(
            1,
            await tenant.ScalarCountAsync(
                "select count(*) from distribution.invitations where id = '" +
                invitation.Id + "'"));
        Assert.Equal(
            1,
            await owner.ScalarCountAsync(
                "select count(*) from distribution.invitations where id = '" +
                invitation.Id + "'"));
        Assert.Equal(1, await owner.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(0, await stranger.ScalarCountAsync("select count(*) from distribution.invitations"));
        Assert.Equal(0, await stranger.ScalarCountAsync("select count(*) from gift_cards.gift_cards"));
        Assert.Equal(0L, (long)(await contextlessInvitation.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Distribution_history_and_invitation_identity_are_database_protected()
    {
        var setup = await PrepareCardAsync();
        var invitation = await DistributeAsync(
            setup,
            RecipientContactType.Email,
            UniqueEmail());
        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);

        foreach (var table in new[] { "invitations", "events" })
        {
            await using var rls = session.Command(
                $"""
                 select relrowsecurity, relforcerowsecurity
                 from pg_class
                 where oid = 'distribution.{table}'::regclass
                 """);
            await using var reader = await rls.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }

        await using var immutableInvitation = session.Command(
            """
            update distribution.invitations
            set recipient_contact = 'changed@example.com'
            where id = @id
            """);
        immutableInvitation.Parameters.AddWithValue("id", invitation.Id);
        var invitationError = await Assert.ThrowsAsync<PostgresException>(
            () => immutableInvitation.ExecuteNonQueryAsync());
        Assert.Equal("55000", invitationError.SqlState);

        await using var eventSession =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, setup.OrganizationId);
        await using var eventMutation = eventSession.Command(
            """
            update distribution.events
            set event_type = 'Claimed'
            where invitation_id = @id
            """);
        eventMutation.Parameters.AddWithValue("id", invitation.Id);
        var eventError = await Assert.ThrowsAsync<PostgresException>(
            () => eventMutation.ExecuteNonQueryAsync());
        Assert.Equal("55000", eventError.SqlState);
    }

    private async Task<CardSetup> PrepareCardAsync()
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
                    expiresAtUtc = DateTimeOffset.UtcNow.AddYears(1),
                    businessReference = "DIST-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "gift-card-" + Guid.NewGuid().ToString("N"),
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
                    businessReference = "DIST-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "allocation-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private async Task<InvitationResponse> DistributeAsync(
        CardSetup setup,
        RecipientContactType contactType,
        string recipient)
    {
        var response = await DistributionClient(setup.OrganizationId).PostAsJsonAsync(
            DistributionRoute(setup.OrganizationId, setup.Card.Id),
            NewDistribution(contactType, recipient));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationResponse>(
            ApiJsonOptions))!;
    }

    private async Task<string> GetClaimTokenAsync(
        Guid organizationId,
        Guid invitationId) =>
        ExtractToken((await GetDeliveryAsync(organizationId, invitationId)).ClaimUrl);

    private async Task<DevelopmentClaimDeliveryResult> GetDeliveryAsync(
        Guid organizationId,
        Guid invitationId)
    {
        var response = await DistributionClient(organizationId).GetAsync(
            $"/api/v1/development/organizations/{organizationId}/" +
            $"claim-deliveries/{invitationId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<DevelopmentClaimDeliveryResult>(ApiJsonOptions))!;
    }

    private async Task<Guid> CreateIdentityUserAsync(string email, string password)
    {
        var response = await PlatformOperator(fixture, PlatformPermissions.UsersCreate)
            .PostAsJsonAsync(
                "/api/v1/users",
                new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserResponse>())!.Id;
    }

    private HttpClient DistributionClient(Guid organizationId) =>
        OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.GiftCardsDistribute);

    private static string DistributionRoute(Guid organizationId, Guid giftCardId) =>
        $"/api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/distributions/";

    private static string ClaimRoute => "/api/v1/gift-card-claims";

    private static DistributionRequest NewDistribution(
        RecipientContactType contactType,
        string recipientContact) =>
        new(
            contactType,
            recipientContact,
            "DIST-" + Guid.NewGuid().ToString("N"),
            "distribution-" + Guid.NewGuid().ToString("N"));

    private static ClaimRequest NewClaim(string token) =>
        new(
            token,
            Password,
            "claim-" + Guid.NewGuid().ToString("N"));

    private static string ExtractToken(string claimUrl)
    {
        var marker = "token=";
        var index = claimUrl.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return Uri.UnescapeDataString(claimUrl[(index + marker.Length)..]);
    }

    private static string TamperToken(string token)
    {
        var secretIndex = token.IndexOf('.', StringComparison.Ordinal) + 1;
        Assert.InRange(secretIndex, 1, token.Length - 1);
        var chars = token.ToCharArray();
        chars[secretIndex] = chars[secretIndex] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    private static string UniqueEmail() =>
        $"recipient-{Guid.NewGuid():N}@example.com";

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static Task<HttpResponseMessage> PostInvalidClaimAsync(
        HttpClient client,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ClaimRoute)
        {
            Content = JsonContent.Create(
                new ClaimRequest(
                    "invalid-claim-token",
                    Password,
                    "claim-" + Guid.NewGuid().ToString("N"))),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private sealed record CardSetup(Guid OrganizationId, CardResponse Card);

    private sealed record DistributionRequest(
        RecipientContactType ContactType,
        string RecipientContact,
        string BusinessReference,
        string IdempotencyKey);

    private sealed record ClaimRequest(
        string ClaimToken,
        string? Password,
        string IdempotencyKey);

    private sealed record InvitationResponse(
        Guid Id,
        Guid FundingOrganizationId,
        Guid IssuingOrganizationId,
        Guid GiftCardId,
        RecipientContactType ContactType,
        string MaskedRecipientContact,
        string State,
        DateTimeOffset ClaimExpiresAtUtc,
        int FailedClaimAttempts,
        string BusinessReference,
        string IdempotencyKey,
        Guid DistributedByUserId,
        Guid DistributedByMembershipId,
        DateTimeOffset DistributedAtUtc,
        Guid? ClaimedByUserId,
        DateTimeOffset? ClaimedAtUtc);

    private sealed record ClaimResponse(
        Guid InvitationId,
        Guid OwnerUserId,
        bool IdentityWasCreated,
        string MaskedLoginIdentifier,
        ClaimSessionResponse? Session,
        CardResponse GiftCard,
        DateTimeOffset ClaimedAtUtc);

    private sealed record ClaimSessionResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAtUtc,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAtUtc);

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
        Guid? DistributionInvitationId,
        DateTimeOffset? DistributedAtUtc,
        DateTimeOffset? ClaimedAtUtc,
        string BusinessReference,
        string IdempotencyKey,
        Guid IssuedByUserId,
        Guid IssuedByMembershipId,
        DateTimeOffset IssuedAtUtc);

    private sealed record UserResponse(Guid Id);

    private sealed class FailingClaimSessionIssuer : IRecipientClaimSessionIssuer
    {
        public Task<TokenPairResult> IssueAsync(
            Guid userId,
            string? password,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected claim-session failure.");
    }

    private sealed class RemoteIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(
            Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(
                    (context, continuation) =>
                    {
                        context.Connection.RemoteIpAddress = remoteIpAddress;
                        return continuation();
                    });
                next(application);
            };
    }
}
