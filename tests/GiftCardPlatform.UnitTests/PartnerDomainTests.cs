using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Partners.Contracts;
using GiftCardPlatform.Modules.Partners.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class PartnerCredentialCodecTests
{
    [Fact]
    public void Create_issues_a_secret_that_matches_its_own_hash()
    {
        var issued = PartnerCredentialCodec.Create();

        Assert.Equal(PartnerCredentialCodec.HashHexLength, issued.Hash.Length);
        Assert.True(PartnerCredentialCodec.Matches(issued.Hash, issued.Secret));
    }

    [Fact]
    public void Create_issues_a_distinct_secret_every_time()
    {
        var secrets = Enumerable.Range(0, 64)
            .Select(_ => PartnerCredentialCodec.Create().Secret)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(64, secrets.Count);
    }

    [Fact]
    public void Issued_secret_is_url_safe_so_it_survives_transport_unencoded()
    {
        var secret = PartnerCredentialCodec.Create().Secret;

        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.DoesNotContain('=', secret);
    }

    [Fact]
    public void A_wrong_secret_does_not_match()
    {
        var issued = PartnerCredentialCodec.Create();
        var other = PartnerCredentialCodec.Create();

        Assert.False(PartnerCredentialCodec.Matches(issued.Hash, other.Secret));
    }

    /// <summary>
    /// An unusable row must be refused exactly like a wrong secret. If a
    /// malformed hash threw, the credential exchange would answer a corrupt row
    /// differently from a wrong secret and become an oracle.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("ABCD")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void A_malformed_stored_hash_never_matches_and_never_throws(string? storedHash)
    {
        var presented = PartnerCredentialCodec.Create().Secret;

        Assert.False(PartnerCredentialCodec.Matches(storedHash, presented));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_presented_secret_never_matches(string? presented)
    {
        var issued = PartnerCredentialCodec.Create();

        Assert.False(PartnerCredentialCodec.Matches(issued.Hash, presented));
    }
}

public sealed class PartnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_normalizes_the_code_to_upper_case()
    {
        var partner = Partner.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), " bynogame ", "BynoGame", Now);

        Assert.Equal("BYNOGAME", partner.Code);
        Assert.Equal("BynoGame", partner.DisplayName);
        Assert.Equal(PartnerStatus.Active, partner.Status);
        Assert.True(partner.IsUsable);
        Assert.Null(partner.DisabledAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("dot.dot")]
    [InlineData("THIS-CODE-IS-DEFINITELY-LONGER-THAN-32-CHARS")]
    public void Register_rejects_an_invalid_code(string? code)
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            Partner.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), code, "Reseller", Now));

        Assert.Equal("partner.code.invalid", exception.Code);
    }

    [Fact]
    public void Register_requires_a_funding_root_organization()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            Partner.Register(Guid.CreateVersion7(), Guid.Empty, "ENEBA", "Eneba", Now));

        Assert.Equal("partner.root_organization.required", exception.Code);
    }

    [Fact]
    public void Register_rejects_a_missing_display_name()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            Partner.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), "ENEBA", "  ", Now));

        Assert.Equal("partner.display_name.invalid", exception.Code);
    }

    [Fact]
    public void Disable_is_the_kill_switch_and_is_idempotent()
    {
        var partner = Partner.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), "KABASAKAL", "Kabasakal", Now);

        partner.Disable(Now);
        var firstDisabledAt = partner.DisabledAtUtc;
        partner.Disable(Now.AddHours(1));

        Assert.Equal(PartnerStatus.Disabled, partner.Status);
        Assert.False(partner.IsUsable);
        Assert.Equal(firstDisabledAt, partner.DisabledAtUtc);
    }

    [Fact]
    public void Reactivate_clears_the_disabled_stamp_so_the_status_check_constraint_holds()
    {
        var partner = Partner.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), "ENEBA", "Eneba", Now);

        partner.Disable(Now);
        partner.Reactivate();

        Assert.Equal(PartnerStatus.Active, partner.Status);
        Assert.Null(partner.DisabledAtUtc);
    }
}

public sealed class PartnerApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    private static PartnerApiClient Register(string? code = "BYNOGAME-PROD", string? hash = null) =>
        RegisterExact(code, hash ?? PartnerCredentialCodec.Create().Hash);

    /// <summary>
    /// Passes the hash through verbatim, so a deliberately null hash is not
    /// silently replaced by a valid one the way the defaulting helper does.
    /// </summary>
    private static PartnerApiClient RegisterExact(string? code, string? hash) =>
        PartnerApiClient.Register(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            code,
            "BynoGame production",
            [PartnerScopes.GiftCardsMint],
            hash!,
            Now);

    [Fact]
    public void Register_stores_only_the_hash_and_starts_active()
    {
        var issued = PartnerCredentialCodec.Create();

        var client = Register(hash: issued.Hash);

        Assert.Equal(issued.Hash, client.SecretHash);
        Assert.DoesNotContain(issued.Secret, client.SecretHash, StringComparison.Ordinal);
        Assert.Equal(PartnerApiClientStatus.Active, client.Status);
        Assert.True(client.IsUsable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ABCDEF")]
    public void Register_rejects_a_secret_hash_of_the_wrong_shape(string? hash)
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            RegisterExact("BYNOGAME-PROD", hash));

        Assert.Equal("partner.api_client.secret.invalid", exception.Code);
    }

    [Fact]
    public void Register_requires_an_owning_partner()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            PartnerApiClient.Register(
                Guid.CreateVersion7(),
                Guid.Empty,
                Guid.CreateVersion7(),
                "BYNOGAME-PROD",
                "BynoGame production",
                [PartnerScopes.GiftCardsMint],
                PartnerCredentialCodec.Create().Hash,
                Now));

        Assert.Equal("partner.api_client.partner.required", exception.Code);
    }

    [Fact]
    public void Register_requires_a_funding_root_organization()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            PartnerApiClient.Register(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.Empty,
                "BYNOGAME-PROD",
                "BynoGame production",
                [PartnerScopes.GiftCardsMint],
                PartnerCredentialCodec.Create().Hash,
                Now));

        Assert.Equal("partner.api_client.root_organization.required", exception.Code);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("")]
    public void Register_rejects_an_invalid_code(string code)
    {
        var exception = Assert.Throws<ValidationFailedException>(() => Register(code));

        Assert.Equal("partner.api_client.code.invalid", exception.Code);
    }

    [Fact]
    public void Register_defaults_are_explicit_and_scopes_are_sorted_and_deduplicated()
    {
        var client = PartnerApiClient.Register(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "BYNOGAME-PROD",
            "BynoGame production",
            [PartnerScopes.GiftCardsMint, PartnerScopes.GiftCardsMint],
            PartnerCredentialCodec.Create().Hash,
            Now);

        Assert.Equal([PartnerScopes.GiftCardsMint], client.Scopes);
    }

    /// <summary>
    /// A credential that authenticates but can do nothing reads as a broken
    /// integration, and an unknown name would leave an operator believing they
    /// granted authority that was never stored.
    /// </summary>
    [Fact]
    public void Register_rejects_an_empty_scope_set()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            PartnerApiClient.Register(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "BYNOGAME-PROD",
                "BynoGame production",
                [],
                PartnerCredentialCodec.Create().Hash,
                Now));

        Assert.Equal("partner.api_client.scopes.invalid", exception.Code);
    }

    [Fact]
    public void Register_rejects_an_unknown_scope()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            PartnerApiClient.Register(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "BYNOGAME-PROD",
                "BynoGame production",
                ["partner.gift_cards.mint.everything"],
                PartnerCredentialCodec.Create().Hash,
                Now));

        Assert.Equal("partner.api_client.scopes.invalid", exception.Code);
    }

    [Fact]
    public void Disable_retires_one_credential_and_is_idempotent()
    {
        var client = Register();

        client.Disable(Now);
        var firstDisabledAt = client.DisabledAtUtc;
        client.Disable(Now.AddHours(1));

        Assert.Equal(PartnerApiClientStatus.Disabled, client.Status);
        Assert.False(client.IsUsable);
        Assert.Equal(firstDisabledAt, client.DisabledAtUtc);
    }
}
