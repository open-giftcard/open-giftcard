using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Payments.Domain;
using GiftCardPlatform.Modules.Payments.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class PaymentTokenIssuanceTests(PlatformApiFixture fixture)
{
    private const string RecipientPassword = "payment recipient passphrase";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Development_OpenAPI_exposes_issuance_without_persisted_secrets()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            out var issuePath));
        Assert.True(issuePath.TryGetProperty("post", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/me/gift-cards/{giftCardId}/payment-tokens/{paymentTokenId}",
            out var statusPath));
        Assert.True(statusPath.TryGetProperty("get", out _));

        var issued = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("IssuedPaymentTokenResult")
            .GetProperty("properties");
        Assert.True(issued.TryGetProperty("rawToken", out _));
        Assert.True(issued.TryGetProperty("numericCode", out _));
        Assert.True(issued.TryGetProperty("expiresAtUtc", out _));
        // The credential carries no card value or balance (ADR-017,
        // DOMAIN_RULES §10.2), and never exposes its stored hash.
        Assert.False(issued.TryGetProperty("secretHash", out _));
        Assert.False(issued.TryGetProperty("balance", out _));
        Assert.False(issued.TryGetProperty("amount", out _));

        var status = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("PaymentTokenStatusResult")
            .GetProperty("properties");
        Assert.True(status.TryGetProperty("state", out _));
        Assert.True(status.TryGetProperty("confirmedAmount", out _));
        Assert.False(status.TryGetProperty("rawToken", out _));
        Assert.False(status.TryGetProperty("numericCode", out _));
        Assert.False(status.TryGetProperty("secretHash", out _));
    }

    [Fact]
    public async Task Owner_receives_a_single_use_credential_valid_for_sixty_seconds()
    {
        var (organizationId, giftCardId, ownerClient) = await ArrangeOwnedCardAsync();

        var before = DateTimeOffset.UtcNow;
        var response = await ownerClient.PostAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            content: null);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var issued = (await response.Content
            .ReadFromJsonAsync<IssuedPaymentTokenResult>(JsonOptions))!;

        Assert.Equal(giftCardId, issued.GiftCardId);
        Assert.NotEqual(Guid.Empty, issued.Id);
        Assert.Equal(60, (issued.ExpiresAtUtc - issued.IssuedAtUtc).TotalSeconds);
        Assert.InRange(issued.IssuedAtUtc, before.AddSeconds(-5), after.AddSeconds(5));

        // Opaque: {tokenId:N}.{43 base64url chars of 256 bits}.
        Assert.StartsWith($"{issued.Id:N}.", issued.RawToken, StringComparison.Ordinal);
        Assert.DoesNotContain(giftCardId.ToString("N"), issued.RawToken, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9]{12}$", issued.NumericCode);

        await AssertOnlyTheHashIsPersistedAsync(organizationId, issued);
    }

    [Fact]
    public async Task Each_request_issues_a_distinct_credential()
    {
        var (_, giftCardId, ownerClient) = await ArrangeOwnedCardAsync();

        var first = await IssueAsync(ownerClient, giftCardId);
        var second = await IssueAsync(ownerClient, giftCardId);

        // Rotation is a client concern (ADR-017): asking again must not return
        // the previous credential.
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.RawToken, second.RawToken);
        Assert.NotEqual(first.NumericCode, second.NumericCode);
    }

    [Fact]
    public async Task A_card_owned_by_someone_else_is_not_found()
    {
        var (_, giftCardId, _) = await ArrangeOwnedCardAsync();
        var (_, _, strangerClient) = await ArrangeOwnedCardAsync();

        var response = await strangerClient.PostAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            content: null);

        // Not 403: a stranger must not learn the card exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        var (_, giftCardId, _) = await ArrangeOwnedCardAsync();

        var response = await fixture.Factory.CreateClient().PostAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_card_still_in_organization_inventory_cannot_be_paid_with()
    {
        var organizationId = await CreateCustomerAsync();
        await AllocateAsync(organizationId, 500m);
        var card = await IssueCardAsync(organizationId, 100m);
        var (_, _, ownerClient) = await ArrangeOwnedCardAsync();

        var response = await ownerClient.PostAsync(
            $"/api/v1/me/gift-cards/{card.Id}/payment-tokens",
            content: null);

        // The caller does not own it, so it is invisible rather than ineligible.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_suspended_card_cannot_be_paid_with()
    {
        var (_, giftCardId, ownerClient) = await ArrangeOwnedCardAsync();
        var suspend = await ownerClient.PostAsJsonAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/lifecycle/suspend",
            new { idempotencyKey = "payment-suspend-" + Guid.NewGuid().ToString("N") });
        suspend.EnsureSuccessStatusCode();

        var response = await ownerClient.PostAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            content: null);

        // The owner may see it, so this is a stateful refusal rather than 404.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_non_transferable_indivisible_card_can_still_be_paid_with()
    {
        // ADR-030 defaults both policies to false. They govern splitting value,
        // not spending it, so an ordinarily issued card must still produce a
        // payment credential.
        var (_, giftCardId, ownerClient) = await ArrangeOwnedCardAsync(
            transferable: false,
            divisible: false);

        var response = await ownerClient.PostAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Row_level_security_hides_tokens_from_a_context_free_connection()
    {
        var (_, giftCardId, ownerClient) = await ArrangeOwnedCardAsync();
        var issued = await IssueAsync(ownerClient, giftCardId);

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from payments.payment_tokens where id = @id",
            connection);
        command.Parameters.AddWithValue("id", issued.Id);

        // Fails closed: no verified session context, no rows.
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Numeric_candidate_scope_exposes_exactly_one_token_to_a_pos_lookup()
    {
        var (_, giftCardId, ownerClient) = await ArrangeOwnedCardAsync();
        var expected = await IssueAsync(ownerClient, giftCardId);
        await IssueAsync(ownerClient, giftCardId);

        var context = new MutableExecutionContext();
        context.SetPosClient(Guid.CreateVersion7(), Guid.CreateVersion7());
        context.SetPaymentCodeCandidate(
            NumericPaymentCodeCodec.Hash(expected.NumericCode));

        await using var scopedConnection = new ScopedDatabaseConnection(
            fixture.AppConnectionString);
        var connection = await scopedConnection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync();
        await new SessionContextWriter().WriteAsync(
            connection,
            transaction,
            context,
            CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "select id from payments.payment_tokens order by id",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected.Id, reader.GetGuid(0));
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();
        await transaction.RollbackAsync();
    }

    private async Task AssertOnlyTheHashIsPersistedAsync(
        Guid organizationId,
        IssuedPaymentTokenResult issued)
    {
        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(
            fixture,
            organizationId);
        await using var command = session.Command(
            """
            select secret_hash, numeric_code_hash, consumed_at_utc
            from payments.payment_tokens
            where id = @id
            """);
        command.Parameters.AddWithValue("id", issued.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var storedHash = reader.GetString(0);
        Assert.Equal(64, storedHash.Length);
        var storedNumericHash = reader.GetString(1);
        Assert.Equal(NumericPaymentCodeCodec.Hash(issued.NumericCode), storedNumericHash);
        Assert.True(await reader.IsDBNullAsync(2));

        // The raw secret must be unrecoverable from storage.
        var rawSecret = issued.RawToken[(issued.RawToken.IndexOf('.', StringComparison.Ordinal) + 1)..];
        Assert.DoesNotContain(rawSecret, storedHash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(issued.RawToken, storedHash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(issued.NumericCode, storedNumericHash, StringComparison.Ordinal);

        await reader.DisposeAsync();
        await using var auditCommand = session.Command(
            """
            select coalesce(metadata::text, '')
            from audit.audit_records
            where entity_id = @id and operation = 'payment.token.issued'
            """);
        auditCommand.Parameters.AddWithValue("id", issued.Id.ToString());
        var metadata = (string)(await auditCommand.ExecuteScalarAsync())!;
        Assert.DoesNotContain(issued.RawToken, metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.NumericCode, metadata, StringComparison.Ordinal);
    }

    private static async Task<IssuedPaymentTokenResult> IssueAsync(
        HttpClient owner,
        Guid giftCardId)
    {
        var response = await owner.PostAsync(
            $"/api/v1/me/gift-cards/{giftCardId}/payment-tokens",
            content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<IssuedPaymentTokenResult>(JsonOptions))!;
    }

    private async Task<(Guid OrganizationId, Guid GiftCardId, HttpClient Owner)>
        ArrangeOwnedCardAsync(bool transferable = true, bool divisible = true)
    {
        var organizationId = await CreateCustomerAsync();
        await AllocateAsync(organizationId, 500m);
        var card = await IssueCardAsync(organizationId, 100m, transferable, divisible);
        var contact = $"payment.recipient.{Guid.NewGuid():N}@example.test";
        var ownerUserId = await DistributeAndClaimAsync(organizationId, card.Id, contact);
        return (organizationId, card.Id, IdentityClient(ownerUserId));
    }

    private HttpClient IdentityClient(Guid userId)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.CreateAccessToken(userId));
        return client;
    }

    private async Task<Guid> CreateCustomerAsync()
    {
        var response = await PlatformOperator(fixture, PlatformPermissions.OrganizationsCreate)
            .PostAsJsonAsync(
                "/api/v1/organizations",
                new
                {
                    name = "Payment Customer " + Guid.NewGuid().ToString("N")[..8],
                    code = "PAY" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(JsonOptions))!.Id;
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
                    businessReference = "PAY-FUND-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "pay-fund-" + Guid.NewGuid().ToString("N"),
                });
        response.EnsureSuccessStatusCode();
    }

    private async Task<GiftCardResult> IssueCardAsync(
        Guid organizationId,
        decimal amount,
        bool transferable = true,
        bool divisible = true)
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
                    isTransferable = transferable,
                    isDivisible = divisible,
                    businessReference = "PAY-CARD-" + Guid.NewGuid().ToString("N"),
                    idempotencyKey = "pay-card-" + Guid.NewGuid().ToString("N"),
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
                businessReference = "PAY-DIST-" + Guid.NewGuid().ToString("N"),
                idempotencyKey = "pay-dist-" + Guid.NewGuid().ToString("N"),
            });
        distribution.EnsureSuccessStatusCode();
        var invitation = (await distribution.Content
            .ReadFromJsonAsync<InvitationResponse>(JsonOptions))!;
        var delivery = await distributor.GetFromJsonAsync<DeliveryResponse>(
            $"/api/v1/development/organizations/{organizationId}/" +
            $"claim-deliveries/{invitation.Id}",
            JsonOptions);
        Assert.NotNull(delivery);

        var claim = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/gift-card-claims",
            new
            {
                claimToken = ExtractToken(delivery!.ClaimUrl),
                password = RecipientPassword,
                idempotencyKey = "pay-claim-" + Guid.NewGuid().ToString("N"),
            });
        claim.EnsureSuccessStatusCode();
        return (await claim.Content
            .ReadFromJsonAsync<InitialClaimResponse>(JsonOptions))!.OwnerUserId;
    }

    private static string ExtractToken(string claimUrl) =>
        Uri.UnescapeDataString(
            claimUrl[(claimUrl.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

    private static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

    private sealed record OrganizationResponse(Guid Id);

    private sealed record InvitationResponse(Guid Id);

    private sealed record DeliveryResponse(string ClaimUrl);

    private sealed record InitialClaimResponse(Guid OwnerUserId);
}
