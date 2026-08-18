using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.AuthorizationTestSupport;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Role creation, permission grants, and assignment (ADR-006), including the
/// rules that keep authorization from becoming an escalation path.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class RoleManagementTests(PlatformApiFixture fixture)
{
    private const string Grantable = OrganizationPermissions.MembershipsView;

    [Fact]
    public async Task A_role_is_created_in_the_callers_organization()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var name = UniqueRoleName("HR");

        var response = await RoleAdmin(fixture, organizationId)
            .PostAsJsonAsync($"/api/v1/organizations/{organizationId}/roles", new { name });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var role = await response.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.Equal(organizationId, role!.OrganizationId);
        Assert.Equal(name, role.Name);
        Assert.Empty(role.Permissions);
    }

    [Fact]
    public async Task Two_organizations_may_use_the_same_role_name()
    {
        var first = await CreateOrganizationAsync(fixture);
        var second = await CreateOrganizationAsync(fixture);
        var name = UniqueRoleName("SHARED");

        var a = await RoleAdmin(fixture, first)
            .PostAsJsonAsync($"/api/v1/organizations/{first}/roles", new { name });
        var b = await RoleAdmin(fixture, second)
            .PostAsJsonAsync($"/api/v1/organizations/{second}/roles", new { name });

        // Uniqueness is per organization, so neither customer can discover the
        // other's role names by provoking a conflict.
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_role_name_in_one_organization_is_rejected()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var name = UniqueRoleName("DUP");
        var client = RoleAdmin(fixture, organizationId);

        await client.PostAsJsonAsync($"/api/v1/organizations/{organizationId}/roles", new { name });
        var second = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles", new { name });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Creating_a_role_requires_the_permission()
    {
        var organizationId = await CreateOrganizationAsync(fixture);

        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.RoleView);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles", new { name = UniqueRoleName() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_role_cannot_be_created_in_another_organization()
    {
        var own = await CreateOrganizationAsync(fixture);
        var other = await CreateOrganizationAsync(fixture);

        var response = await RoleAdmin(fixture, own)
            .PostAsJsonAsync($"/api/v1/organizations/{other}/roles", new { name = UniqueRoleName() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Granted_permissions_are_returned_and_listed()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);

        Assert.Contains(Grantable, role.Permissions);

        var listed = await RoleAdmin(fixture, organizationId)
            .GetFromJsonAsync<List<RoleResponse>>($"/api/v1/organizations/{organizationId}/roles");

        Assert.Contains(listed!, r => r.Id == role.Id && r.Permissions.Contains(Grantable));
    }

    [Fact]
    public async Task Granting_the_same_permission_twice_is_idempotent()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);

        var response = await RoleAdmin(fixture, organizationId, Grantable).PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/{role.Id}/permissions",
            new { permissions = new[] { Grantable } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.Single(updated!.Permissions);
    }

    [Fact]
    public async Task An_unknown_permission_is_rejected_against_the_catalogue()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var role = await CreateRoleAsync(fixture, organizationId);

        // A caller cannot manufacture an unknown permission: the seeded
        // catalogue is checked before effective-permission evaluation.
        var client = OrganizationMember(
            fixture,
            organizationId,
            [.. RoleAdminPermissions, "organization.not_a_real_permission"]);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/{role.Id}/permissions",
            new { permissions = new[] { "organization.not_a_real_permission" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_platform_permission_cannot_be_granted_to_an_organization_role()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var role = await CreateRoleAsync(fixture, organizationId);

        var client = OrganizationMember(
            fixture,
            organizationId,
            [.. RoleAdminPermissions, PlatformPermissions.OrganizationsCreate]);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/{role.Id}/permissions",
            new { permissions = new[] { PlatformPermissions.OrganizationsCreate } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_caller_cannot_grant_a_permission_it_does_not_hold()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var role = await CreateRoleAsync(fixture, organizationId);

        // Role administrator, but does not itself hold the permission being
        // granted (DOMAIN_RULES §4.7).
        var response = await RoleAdmin(fixture, organizationId).PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/{role.Id}/permissions",
            new { permissions = new[] { OrganizationPermissions.MembershipsDisable } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_role_is_assigned_to_a_membership()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);

        var assignment = await AssignRoleAsync(
            fixture, organizationId, membership.Id, role.Id, RoleScope.Organization, organizationId);

        Assert.Equal(membership.Id, assignment.MembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal("Organization", assignment.Scope);
    }

    [Fact]
    public async Task Role_assignments_are_listed_in_stable_organization_order()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var firstMembership = await CreateMembershipAsync(fixture, organizationId);
        var secondMembership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);
        var first = await AssignRoleAsync(
            fixture,
            organizationId,
            firstMembership.Id,
            role.Id,
            RoleScope.Organization,
            organizationId);
        var second = await AssignRoleAsync(
            fixture,
            organizationId,
            secondMembership.Id,
            role.Id,
            RoleScope.Organization,
            organizationId);

        var response = await RoleAdmin(fixture, organizationId)
            .GetAsync($"/api/v1/organizations/{organizationId}/roles/assignments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assignments = await response.Content
            .ReadFromJsonAsync<List<RoleAssignmentResponse>>();
        Assert.NotNull(assignments);
        var listed = assignments!
            .Where(assignment => assignment.Id == first.Id || assignment.Id == second.Id)
            .ToArray();
        Assert.Equal([first.Id, second.Id], listed.Select(assignment => assignment.Id));
    }

    [Fact]
    public async Task Listing_role_assignments_requires_view_and_exact_organization()
    {
        var own = await CreateOrganizationAsync(fixture);
        var other = await CreateOrganizationAsync(fixture);

        var noView = OrganizationMember(
            fixture,
            own,
            OrganizationPermissions.RoleAssign);
        var missingPermission = await noView.GetAsync(
            $"/api/v1/organizations/{own}/roles/assignments");
        Assert.Equal(HttpStatusCode.Forbidden, missingPermission.StatusCode);

        var crossOrganization = await RoleAdmin(fixture, own).GetAsync(
            $"/api/v1/organizations/{other}/roles/assignments");
        Assert.Equal(HttpStatusCode.Forbidden, crossOrganization.StatusCode);
    }

    [Fact]
    public async Task A_role_from_another_organization_cannot_be_assigned()
    {
        var own = await CreateOrganizationAsync(fixture);
        var other = await CreateOrganizationAsync(fixture);

        var membership = await CreateMembershipAsync(fixture, own);
        var foreignRole = await CreateRoleAsync(fixture, other, Grantable);

        var response = await RoleAdmin(fixture, own).PostAsJsonAsync(
            $"/api/v1/organizations/{own}/roles/assignments",
            new
            {
                membershipId = membership.Id,
                roleId = foreignRole.Id,
                scope = "Organization",
            });

        // RLS hides the other tenant's role entirely, so the answer is "not
        // found" rather than "forbidden" — a refusal must not confirm that the
        // role exists (DOMAIN_RULES §4.10).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_same_role_cannot_be_assigned_twice_at_one_anchor()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);

        await AssignRoleAsync(
            fixture, organizationId, membership.Id, role.Id, RoleScope.Organization, organizationId);

        var response = await RoleAdmin(fixture, organizationId).PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/assignments",
            new
            {
                membershipId = membership.Id,
                roleId = role.Id,
                scope = "Organization",
                anchorOrganizationId = organizationId,
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Selected_organizations_scope_requires_at_least_one_organization()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);

        var response = await RoleAdmin(fixture, organizationId).PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/assignments",
            new
            {
                membershipId = membership.Id,
                roleId = role.Id,
                scope = "SelectedOrganizations",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Role_creation_and_assignment_write_audit_records()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId, Grantable);
        var assignment = await AssignRoleAsync(
            fixture, organizationId, membership.Id, role.Id, RoleScope.Organization, organizationId);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);

        foreach (var (operation, entityId) in new[]
                 {
                     (AuditOperations.RoleCreated, role.Id.ToString()),
                     (AuditOperations.RolePermissionsGranted, role.Id.ToString()),
                     (AuditOperations.RoleAssigned, assignment.Id.ToString()),
                 })
        {
            await using var command = session.Command(
                """
                select count(*) from audit.audit_records
                where operation = @operation and entity_id = @entity_id and outcome = 'Success'
                """);
            command.Parameters.AddWithValue("operation", operation);
            command.Parameters.AddWithValue("entity_id", entityId);

            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Role_creation_and_its_audit_record_commit_atomically()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var name = UniqueRoleName("ATOMIC");
        var actorUserId = Guid.CreateVersion7();
        await ProvisionOrganizationActorAsync(
            fixture,
            actorUserId,
            organizationId,
            RoleAdminPermissions);

        using var factory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.ConfigureServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAuditRecorder));
                services.Remove(original);

                services.Add(ServiceDescriptor.Describe(
                    typeof(IAuditRecorder),
                    sp =>
                    {
                        var inner = (IAuditRecorder)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                        return new FailAfterWritingAuditRecorder(inner);
                    },
                    original.Lifetime));
            }));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(actorUserId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles", new { name });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // The role row must not survive the rolled-back unit.
        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);
        Assert.Equal(0L, await session.ScalarCountAsync(
            """select count(*) from "authorization".roles where name = @name""",
            command => command.Parameters.AddWithValue("name", name)));
    }

    private sealed class FailAfterWritingAuditRecorder(IAuditRecorder inner) : IAuditRecorder
    {
        public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            await inner.RecordAsync(entry, cancellationToken);
            throw new InvalidOperationException("Simulated audit failure after the audit row was written.");
        }

        public Task RecordIndependentlyAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            inner.RecordIndependentlyAsync(entry, cancellationToken);
    }
}
