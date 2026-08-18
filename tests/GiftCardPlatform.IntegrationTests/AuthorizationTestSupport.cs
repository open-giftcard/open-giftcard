using System.Net.Http.Json;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.Extensions.DependencyInjection;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

internal static class AuthorizationTestSupport
{
    internal sealed record RoleResponse(
        Guid Id,
        Guid OrganizationId,
        string Name,
        IReadOnlyList<string> Permissions,
        DateTimeOffset CreatedAtUtc);

    internal sealed record RoleAssignmentResponse(
        Guid Id,
        Guid OrganizationId,
        Guid MembershipId,
        Guid RoleId,
        string Scope,
        Guid AnchorOrganizationId,
        IReadOnlyList<Guid> SelectedOrganizationIds,
        DateTimeOffset CreatedAtUtc);

    /// <summary>All four role-management permissions, for a caller acting as a company administrator.</summary>
    public static string[] RoleAdminPermissions =>
    [
        OrganizationPermissions.RoleCreate,
        OrganizationPermissions.RoleView,
        OrganizationPermissions.RoleAssign,
        OrganizationPermissions.RoleManagePermissions,
    ];

    /// <summary>
    /// A role administrator that also holds <paramref name="grantable"/>, because
    /// a caller may not grant a permission it does not itself hold.
    /// </summary>
    public static HttpClient RoleAdmin(
        PlatformApiFixture fixture,
        Guid organizationId,
        params string[] grantable) =>
        OrganizationMember(fixture, organizationId, [.. RoleAdminPermissions, .. grantable]);

    public static string UniqueRoleName(string prefix = "ROLE") =>
        prefix + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>Creates a role and grants it the given permissions.</summary>
    public static async Task<RoleResponse> CreateRoleAsync(
        PlatformApiFixture fixture,
        Guid organizationId,
        params string[] permissions)
    {
        var client = RoleAdmin(fixture, organizationId, permissions);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles",
            new { name = UniqueRoleName() });
        created.EnsureSuccessStatusCode();

        var role = (await created.Content.ReadFromJsonAsync<RoleResponse>())!;

        if (permissions.Length == 0)
        {
            return role;
        }

        var granted = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/{role.Id}/permissions",
            new { permissions });
        granted.EnsureSuccessStatusCode();

        return (await granted.Content.ReadFromJsonAsync<RoleResponse>())!;
    }

    /// <summary>Assigns a role to a membership at a scope.</summary>
    public static async Task<RoleAssignmentResponse> AssignRoleAsync(
        PlatformApiFixture fixture,
        Guid organizationId,
        Guid membershipId,
        Guid roleId,
        RoleScope scope,
        Guid? anchorOrganizationId = null,
        IReadOnlyList<Guid>? selectedOrganizationIds = null)
    {
        var client = RoleAdmin(fixture, organizationId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/roles/assignments",
            new
            {
                membershipId,
                roleId,
                scope = scope.ToString(),
                anchorOrganizationId,
                selectedOrganizationIds,
            });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<RoleAssignmentResponse>())!;
    }

    /// <summary>
    /// Resolves effective permissions through the module's own evaluator, acting
    /// as <paramref name="actingOrganizationId"/> so RLS applies exactly as it
    /// would in a request.
    /// </summary>
    public static async Task<IReadOnlySet<string>> EvaluateAsync(
        PlatformApiFixture fixture,
        Guid actingOrganizationId,
        Guid membershipId,
        Guid targetOrganizationId)
    {
        // Async scope: the scoped database connection is IAsyncDisposable only.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();

        var executionContext = scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
        executionContext.SetOrganizationMember(
            Guid.CreateVersion7(),
            membershipId,
            actingOrganizationId);

        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();

        return await evaluator.GetEffectivePermissionsAsync(
            membershipId, targetOrganizationId, CancellationToken.None);
    }
}
