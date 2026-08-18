using System.Text.Json;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Guards the combined portal and cardholder client contract against a
/// convergence merge accidentally retaining only one feature branch.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class ClientContractConvergenceTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Development_OpenAPI_exposes_cardholder_and_portal_contracts_together()
    {
        var response = await fixture.Factory.CreateClient()
            .GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var schemas = root
            .GetProperty("components")
            .GetProperty("schemas");

        var claimProperties = schemas
            .GetProperty("GiftCardClaimApiResponse")
            .GetProperty("properties");
        Assert.True(claimProperties.TryGetProperty("session", out _));

        var membershipInput = schemas
            .GetProperty("CreateMembershipApiRequest")
            .GetProperty("properties");
        Assert.True(membershipInput.TryGetProperty("userId", out _));
        Assert.True(membershipInput.TryGetProperty("email", out _));

        var membershipOutput = schemas
            .GetProperty("MembershipApiResponse")
            .GetProperty("properties");
        Assert.True(membershipOutput.TryGetProperty("email", out _));

        var assignments = root
            .GetProperty("paths")
            .GetProperty(
                "/api/v1/organizations/{organizationId}/roles/assignments");
        Assert.True(assignments.TryGetProperty("get", out _));
        Assert.True(assignments.TryGetProperty("post", out _));
    }
}
