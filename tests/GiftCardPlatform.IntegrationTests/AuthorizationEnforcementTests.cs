using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.AuthorizationTestSupport;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// IMPL-005 proofs: authentication resolves a real active membership and
/// application services authorize only database-backed scoped assignments.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class AuthorizationEnforcementTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Retired_permission_header_cannot_grant_access()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();
        await ProvisionOrganizationActorAsync(fixture, userId, organizationId, []);

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());
        client.DefaultRequestHeaders.Add(
            UntrustedPermissionsHeader,
            OrganizationPermissions.MembershipsView);

        var response = await client.GetAsync(
            $"/api/v1/organizations/{organizationId}/memberships");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_membership_cannot_authenticate()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(
            fixture,
            userId,
            organizationId,
            [OrganizationPermissions.MembershipsView]);

        await SetMembershipStatusAsync(organizationId, membershipId, "Disabled");

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());

        var response = await client.GetAsync(
            $"/api/v1/organizations/{organizationId}/memberships");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task User_cannot_select_another_users_membership()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await ProvisionOrganizationActorAsync(
            fixture,
            Guid.CreateVersion7(),
            organizationId,
            [OrganizationPermissions.MembershipsView]);

        var otherUserId = Guid.CreateVersion7();
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(otherUserId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());

        var response = await client.GetAsync(
            $"/api/v1/organizations/{organizationId}/memberships");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Subtree_scope_is_enforced_at_the_service_boundary()
    {
        var root = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(root);
        var actorUserId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(fixture, actorUserId, root, []);
        var role = await CreateRoleAsync(fixture, root, OrganizationPermissions.View);

        await AssignRoleAsync(
            fixture,
            root,
            membershipId,
            role.Id,
            RoleScope.Subtree,
            root);

        var response = await Actor(actorUserId, root)
            .GetAsync($"/api/v1/organizations/{child}/subsidiaries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Organization_scope_does_not_reach_a_descendant_service_target()
    {
        var root = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(root);
        var actorUserId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(fixture, actorUserId, root, []);
        var role = await CreateRoleAsync(fixture, root, OrganizationPermissions.View);

        await AssignRoleAsync(
            fixture,
            root,
            membershipId,
            role.Id,
            RoleScope.Organization,
            root);

        var response = await Actor(actorUserId, root)
            .GetAsync($"/api/v1/organizations/{child}/subsidiaries");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Selected_organizations_scope_reaches_only_the_selected_service_target()
    {
        var root = await CreateOrganizationAsync(fixture);
        var selected = await CreateSubsidiaryAsync(root);
        var unselected = await CreateSubsidiaryAsync(root);
        var actorUserId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(fixture, actorUserId, root, []);
        var role = await CreateRoleAsync(fixture, root, OrganizationPermissions.View);

        await AssignRoleAsync(
            fixture,
            root,
            membershipId,
            role.Id,
            RoleScope.SelectedOrganizations,
            root,
            [selected]);

        Assert.Equal(
            HttpStatusCode.OK,
            (await Actor(actorUserId, root)
                .GetAsync($"/api/v1/organizations/{selected}/subsidiaries")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Actor(actorUserId, root)
                .GetAsync($"/api/v1/organizations/{unselected}/subsidiaries")).StatusCode);
    }

    [Fact]
    public async Task Subtree_scope_can_manage_memberships_in_a_descendant()
    {
        var root = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(root);
        var actorUserId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(
            fixture,
            actorUserId,
            root,
            []);
        var role = await CreateRoleAsync(
            fixture,
            root,
            OrganizationPermissions.MembershipsCreate,
            OrganizationPermissions.MembershipsView);

        await AssignRoleAsync(
            fixture,
            root,
            membershipId,
            role.Id,
            RoleScope.Subtree,
            root);

        var actor = Actor(actorUserId, root);
        var targetUserId = Guid.CreateVersion7();
        var created = await actor.PostAsJsonAsync(
            $"/api/v1/organizations/{child}/memberships",
            new { userId = targetUserId });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var listed = await actor.GetFromJsonAsync<PagedResponse<MembershipResponse>>(
            $"/api/v1/organizations/{child}/memberships");
        Assert.Contains(
            listed!.Items,
            membership => membership.UserId == targetUserId &&
                          membership.OrganizationId == child);
    }

    [Fact]
    public async Task Subtree_scope_can_manage_roles_in_a_descendant()
    {
        var root = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(root);
        var actorUserId = Guid.CreateVersion7();
        var membershipId = await ProvisionOrganizationActorAsync(
            fixture,
            actorUserId,
            root,
            []);
        var role = await CreateRoleAsync(
            fixture,
            root,
            OrganizationPermissions.RoleCreate,
            OrganizationPermissions.RoleView);

        await AssignRoleAsync(
            fixture,
            root,
            membershipId,
            role.Id,
            RoleScope.Subtree,
            root);

        var actor = Actor(actorUserId, root);
        var roleName = UniqueRoleName("CHILD");
        var created = await actor.PostAsJsonAsync(
            $"/api/v1/organizations/{child}/roles",
            new { name = roleName });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var listed = await actor.GetFromJsonAsync<IReadOnlyList<RoleResponse>>(
            $"/api/v1/organizations/{child}/roles");
        Assert.Contains(
            listed!,
            descendantRole => descendantRole.OrganizationId == child &&
                              descendantRole.Name == roleName);
    }

    private HttpClient Actor(Guid userId, Guid organizationId)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());
        return client;
    }

    private async Task<Guid> CreateSubsidiaryAsync(Guid parentId)
    {
        var client = OrganizationMember(
            fixture,
            parentId,
            OrganizationPermissions.CreateSubsidiary);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new
            {
                name = "Authorization Scope Subsidiary",
                code = "AUT" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<OrganizationIdResponse>())!.Id;
    }

    private async Task SetMembershipStatusAsync(
        Guid organizationId,
        Guid membershipId,
        string status)
    {
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, transaction, organizationId, isPlatformOperator: false);

        await using var command = new NpgsqlCommand(
            """
            update organizations.organization_memberships
            set status = @status, disabled_at_utc = now()
            where id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", membershipId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private sealed record OrganizationIdResponse(Guid Id);
}
