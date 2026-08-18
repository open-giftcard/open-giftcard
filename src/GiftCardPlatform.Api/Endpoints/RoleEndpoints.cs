using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

/// <summary>Explicit API request contract. Never an EF Core entity.</summary>
public sealed record CreateRoleApiRequest
{
    /// <example>HR</example>
    public string? Name { get; init; }
}

/// <summary>Explicit API request contract.</summary>
public sealed record GrantPermissionsApiRequest
{
    /// <example>["organization.memberships.create","organization.memberships.view"]</example>
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>Explicit API request contract.</summary>
public sealed record AssignRoleApiRequest
{
    public Guid MembershipId { get; init; }

    public Guid RoleId { get; init; }

    /// <example>Subtree</example>
    public RoleScope Scope { get; init; } = RoleScope.Organization;

    /// <summary>Defaults to the caller's active organization when omitted.</summary>
    public Guid? AnchorOrganizationId { get; init; }

    /// <summary>Required for, and only valid with, SelectedOrganizations scope.</summary>
    public IReadOnlyList<Guid>? SelectedOrganizationIds { get; init; }
}

/// <summary>Explicit API response contract.</summary>
public sealed record RoleApiResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAtUtc);

/// <summary>Explicit API response contract.</summary>
public sealed record RoleAssignmentApiResponse(
    Guid Id,
    Guid OrganizationId,
    Guid MembershipId,
    Guid RoleId,
    string Scope,
    Guid AnchorOrganizationId,
    IReadOnlyList<Guid> SelectedOrganizationIds,
    DateTimeOffset CreatedAtUtc);

internal static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/roles")
            .WithTags("Roles")
            .RequireAuthorization();

        group.MapPost("/", CreateAsync)
            .WithName("CreateRole")
            .WithSummary("Creates a role in an authorized organization.")
            .WithDescription(
                "Requires the role.create permission. A role belongs to exactly one organization and " +
                "carries no scope of its own — scope lives on the assignment (ADR-006).")
            .Produces<RoleApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithName("ListRoles")
            .WithSummary("Lists roles in an authorized organization.")
            .WithDescription("Requires the role.view permission.")
            .Produces<IReadOnlyList<RoleApiResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{roleId:guid}/permissions", GrantAsync)
            .WithName("GrantRolePermissions")
            .WithSummary("Grants permissions to a role.")
            .WithDescription(
                "Requires the role.manage_permissions permission. Only organization permissions the " +
                "caller itself holds may be granted, and only names present in the catalogue.")
            .Produces<RoleApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/assignments", AssignAsync)
            .WithName("AssignRole")
            .WithSummary("Assigns a role to a membership within a scope.")
            .WithDescription(
                "Requires the role.assign permission. A role from one organization can never be " +
                "assigned to a membership in another.")
            .Produces<RoleAssignmentApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/assignments", ListAssignmentsAsync)
            .WithName("ListRoleAssignments")
            .WithSummary("Lists role assignments in an authorized organization.")
            .WithDescription(
                "Requires the role.view permission. Results are scoped to the exact organization " +
                "and ordered by creation time with a stable identifier tie breaker.")
            .Produces<IReadOnlyList<RoleAssignmentApiResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        [FromBody] CreateRoleApiRequest request,
        IRoleService roleService,
        CancellationToken cancellationToken)
    {
        var result = await roleService.CreateRoleAsync(
            organizationId, new CreateRoleRequest(request.Name), cancellationToken);

        var response = ToResponse(result);
        return Results.Created($"{ApiRoutes.V1}/organizations/{organizationId}/roles/{response.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        IRoleService roleService,
        CancellationToken cancellationToken)
    {
        var results = await roleService.ListRolesAsync(organizationId, cancellationToken);
        return Results.Ok(results.Select(ToResponse));
    }

    private static async Task<IResult> GrantAsync(
        Guid organizationId,
        Guid roleId,
        [FromBody] GrantPermissionsApiRequest request,
        IRoleService roleService,
        CancellationToken cancellationToken)
    {
        var result = await roleService.GrantPermissionsAsync(
            organizationId, roleId, new GrantPermissionsRequest(request.Permissions), cancellationToken);

        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> AssignAsync(
        Guid organizationId,
        [FromBody] AssignRoleApiRequest request,
        IRoleService roleService,
        CancellationToken cancellationToken)
    {
        var result = await roleService.AssignRoleAsync(
            organizationId,
            new AssignRoleRequest(
                request.MembershipId,
                request.RoleId,
                request.Scope,
                request.AnchorOrganizationId,
                request.SelectedOrganizationIds),
            cancellationToken);

        var response = new RoleAssignmentApiResponse(
            result.Id,
            result.OrganizationId,
            result.MembershipId,
            result.RoleId,
            result.Scope,
            result.AnchorOrganizationId,
            result.SelectedOrganizationIds,
            result.CreatedAtUtc);

        return Results.Created(
            $"{ApiRoutes.V1}/organizations/{organizationId}/roles/assignments/{response.Id}",
            response);
    }

    private static async Task<IResult> ListAssignmentsAsync(
        Guid organizationId,
        IRoleService roleService,
        CancellationToken cancellationToken)
    {
        var results = await roleService.ListRoleAssignmentsAsync(
            organizationId,
            cancellationToken);
        return Results.Ok(results.Select(ToResponse));
    }

    private static RoleAssignmentApiResponse ToResponse(
        RoleAssignmentResult result) => new(
        result.Id,
        result.OrganizationId,
        result.MembershipId,
        result.RoleId,
        result.Scope,
        result.AnchorOrganizationId,
        result.SelectedOrganizationIds,
        result.CreatedAtUtc);

    private static RoleApiResponse ToResponse(RoleResult result) => new(
        result.Id,
        result.OrganizationId,
        result.Name,
        result.Permissions,
        result.CreatedAtUtc);
}
