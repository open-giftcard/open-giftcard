using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using static GiftCardPlatform.IntegrationTests.AuthorizationTestSupport;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Effective-permission resolution across the three ADR-006 scope types.
///
/// The rule these protect: parent-organization ownership alone grants nothing.
/// Only an assignment whose scope actually reaches the target organization does
/// (DOMAIN_RULES §4.8).
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class PermissionEvaluationTests(PlatformApiFixture fixture)
{
    private const string Granted = OrganizationPermissions.MembershipsView;

    /// <summary>Root with a child and a grandchild, so subtree depth is exercised.</summary>
    private async Task<(Guid Root, Guid Child, Guid Grandchild)> CreateHierarchyAsync()
    {
        var root = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(root);
        var grandchild = await CreateSubsidiaryAsync(child);
        return (root, child, grandchild);
    }

    private async Task<Guid> CreateSubsidiaryAsync(Guid parentId)
    {
        var client = OrganizationMember(fixture, parentId, OrganizationPermissions.CreateSubsidiary);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new
            {
                name = "Scope Subsidiary",
                code = "SCP" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<SubsidiaryIdResponse>();
        return body!.Id;
    }

    private sealed record SubsidiaryIdResponse(Guid Id);

    [Fact]
    public async Task A_membership_with_no_assignment_has_no_permissions()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);

        var effective = await EvaluateAsync(fixture, organizationId, membership.Id, organizationId);

        Assert.Empty(effective);
    }

    [Fact]
    public async Task Organization_scope_grants_at_the_anchor_only()
    {
        var hierarchy = await CreateHierarchyAsync();
        var membership = await CreateMembershipAsync(fixture, hierarchy.Root);
        var role = await CreateRoleAsync(fixture, hierarchy.Root, Granted);

        await AssignRoleAsync(
            fixture, hierarchy.Root, membership.Id, role.Id, RoleScope.Organization, hierarchy.Root);

        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Root));

        // A descendant is not reached: hierarchy alone confers nothing.
        Assert.DoesNotContain(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Child));
        Assert.DoesNotContain(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Grandchild));
    }

    [Fact]
    public async Task Subtree_scope_grants_at_the_anchor_and_every_descendant()
    {
        var hierarchy = await CreateHierarchyAsync();
        var membership = await CreateMembershipAsync(fixture, hierarchy.Root);
        var role = await CreateRoleAsync(fixture, hierarchy.Root, Granted);

        await AssignRoleAsync(
            fixture, hierarchy.Root, membership.Id, role.Id, RoleScope.Subtree, hierarchy.Root);

        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Root));
        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Child));
        // Depth is not special-cased: the ltree path covers the whole subtree.
        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Grandchild));
    }

    [Fact]
    public async Task Subtree_scope_anchored_at_a_child_does_not_reach_upwards()
    {
        var hierarchy = await CreateHierarchyAsync();
        var membership = await CreateMembershipAsync(fixture, hierarchy.Root);
        var role = await CreateRoleAsync(fixture, hierarchy.Root, Granted);

        await AssignRoleAsync(
            fixture, hierarchy.Root, membership.Id, role.Id, RoleScope.Subtree, hierarchy.Child);

        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Child));
        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Grandchild));

        // Scope reaches down, never up.
        Assert.DoesNotContain(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Root));
    }

    [Fact]
    public async Task Subtree_scope_does_not_reach_a_different_branch()
    {
        var root = await CreateOrganizationAsync(fixture);
        var first = await CreateSubsidiaryAsync(root);
        var second = await CreateSubsidiaryAsync(root);

        var membership = await CreateMembershipAsync(fixture, root);
        var role = await CreateRoleAsync(fixture, root, Granted);

        await AssignRoleAsync(fixture, root, membership.Id, role.Id, RoleScope.Subtree, first);

        Assert.Contains(Granted, await EvaluateAsync(fixture, root, membership.Id, first));
        Assert.DoesNotContain(Granted, await EvaluateAsync(fixture, root, membership.Id, second));
    }

    [Fact]
    public async Task Selected_organizations_scope_grants_only_at_the_listed_organizations()
    {
        var hierarchy = await CreateHierarchyAsync();
        var membership = await CreateMembershipAsync(fixture, hierarchy.Root);
        var role = await CreateRoleAsync(fixture, hierarchy.Root, Granted);

        await AssignRoleAsync(
            fixture,
            hierarchy.Root,
            membership.Id,
            role.Id,
            RoleScope.SelectedOrganizations,
            hierarchy.Root,
            [hierarchy.Grandchild]);

        Assert.Contains(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Grandchild));

        // Neither the anchor nor the intermediate organization is implied.
        Assert.DoesNotContain(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Root));
        Assert.DoesNotContain(Granted, await EvaluateAsync(fixture, hierarchy.Root, membership.Id, hierarchy.Child));
    }

    [Fact]
    public async Task Effective_permissions_are_the_union_of_every_covering_assignment()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);

        var viewer = await CreateRoleAsync(fixture, organizationId, OrganizationPermissions.MembershipsView);
        var creator = await CreateRoleAsync(fixture, organizationId, OrganizationPermissions.MembershipsCreate);

        await AssignRoleAsync(
            fixture, organizationId, membership.Id, viewer.Id, RoleScope.Organization, organizationId);
        await AssignRoleAsync(
            fixture, organizationId, membership.Id, creator.Id, RoleScope.Organization, organizationId);

        var effective = await EvaluateAsync(fixture, organizationId, membership.Id, organizationId);

        Assert.Contains(OrganizationPermissions.MembershipsView, effective);
        Assert.Contains(OrganizationPermissions.MembershipsCreate, effective);
    }

    [Fact]
    public async Task A_role_with_no_permissions_grants_nothing()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId);

        await AssignRoleAsync(
            fixture, organizationId, membership.Id, role.Id, RoleScope.Organization, organizationId);

        Assert.Empty(await EvaluateAsync(fixture, organizationId, membership.Id, organizationId));
    }

    [Fact]
    public async Task Another_tenants_assignments_are_invisible_to_the_evaluator()
    {
        var tenantA = await CreateOrganizationAsync(fixture);
        var tenantB = await CreateOrganizationAsync(fixture);

        var membership = await CreateMembershipAsync(fixture, tenantA);
        var role = await CreateRoleAsync(fixture, tenantA, Granted);
        await AssignRoleAsync(fixture, tenantA, membership.Id, role.Id, RoleScope.Organization, tenantA);

        // Evaluated while acting as the other tenant: RLS hides the assignment,
        // so no permission is resolved.
        var effective = await EvaluateAsync(fixture, tenantB, membership.Id, tenantA);

        Assert.Empty(effective);
    }
}
