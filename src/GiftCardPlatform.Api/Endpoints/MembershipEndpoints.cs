using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

/// <summary>Explicit API request contract. Never an EF Core entity.</summary>
public sealed record CreateMembershipApiRequest
{
    /// <example>018f9a2b-4c6d-7e8f-90a1-b2c3d4e5f601</example>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Existing active staff account email. Exactly one of email or userId is
    /// required.
    /// </summary>
    /// <example>company.admin@example.com</example>
    public string? Email { get; init; }
}

/// <summary>
/// One page of results. Shared by the list endpoints so paging looks the same
/// across the API.
/// </summary>
public sealed record PagedApiResponse<T>(IReadOnlyList<T> Items, int Limit, int Offset, bool HasMore);

/// <summary>Explicit API response contract.</summary>
public sealed record MembershipApiResponse(
    Guid Id,
    Guid OrganizationId,
    Guid UserId,
    string? Email,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DisabledAtUtc);

internal static class MembershipEndpoints
{
    public static IEndpointRouteBuilder MapMembershipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/memberships")
            .WithTags("Memberships")
            .RequireAuthorization();

        group.MapPost("/", CreateAsync)
            .WithName("CreateMembership")
            .WithSummary("Creates a membership in the caller's organization.")
            .WithDescription(
                "Requires the organization.memberships.create permission. The organization is taken " +
                "from the route and must be covered by the verified membership's scope. The membership and its " +
                "append-only audit record are committed atomically. Exactly one existing-user selector is " +
                "required: userId remains supported for compatibility, while email is resolved only after " +
                "authorization.")
            .Produces<MembershipApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithName("ListMemberships")
            .WithSummary("Lists memberships for an organization.")
            .WithDescription(
                "An organization-scoped caller requires organization.memberships.view over the exact target. " +
                "A platform operator may read any organization " +
                "through the controlled RLS path (platform.organizations.memberships.view). " +
                "Paged via limit (1-200, default 50) and offset.")
            .Produces<PagedApiResponse<MembershipApiResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{membershipId:guid}/disable", DisableAsync)
            .WithName("DisableMembership")
            .WithSummary("Disables a membership in an authorized organization.")
            .WithDescription("Requires the organization.memberships.disable permission.")
            .Produces<MembershipApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        [FromBody] CreateMembershipApiRequest request,
        IMembershipService membershipService,
        IOrganizationStaffDirectory staffDirectory,
        CancellationToken cancellationToken)
    {
        var (userId, email) = await ResolveUserSelectorAsync(
            organizationId,
            request,
            staffDirectory,
            cancellationToken);
        var result = await membershipService.CreateMembershipAsync(
            organizationId,
            new CreateMembershipRequest(userId),
            cancellationToken);

        var response = ToResponse(result, email);
        return Results.Created(
            $"{ApiRoutes.V1}/organizations/{response.OrganizationId}/memberships/{response.Id}",
            response);
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        IMembershipService membershipService,
        IOrganizationStaffDirectory staffDirectory,
        CancellationToken cancellationToken,
        int? limit = null,
        int? offset = null)
    {
        var page = new PageRequest(limit ?? PageRequest.DefaultLimit, offset ?? 0);
        var results = await membershipService.ListMembershipsAsync(organizationId, page, cancellationToken);
        var emails = await staffDirectory.GetVisibleEmailsAsync(
            organizationId,
            results.Items.Select(item => item.UserId).ToArray(),
            cancellationToken);

        return Results.Ok(new PagedApiResponse<MembershipApiResponse>(
            [.. results.Items.Select(item =>
                ToResponse(
                    item,
                    emails.GetValueOrDefault(item.UserId)))],
            results.Limit,
            results.Offset,
            results.HasMore));
    }

    private static async Task<IResult> DisableAsync(
        Guid organizationId,
        Guid membershipId,
        IMembershipService membershipService,
        CancellationToken cancellationToken)
    {
        var result = await membershipService.DisableMembershipAsync(organizationId, membershipId, cancellationToken);
        return Results.Ok(ToResponse(result, email: null));
    }

    private static async Task<(Guid UserId, string? Email)> ResolveUserSelectorAsync(
        Guid organizationId,
        CreateMembershipApiRequest request,
        IOrganizationStaffDirectory staffDirectory,
        CancellationToken cancellationToken)
    {
        var hasUserId = request.UserId.HasValue;
        var hasEmail = !string.IsNullOrWhiteSpace(request.Email);
        if (hasUserId == hasEmail)
        {
            throw new ValidationFailedException(
                "membership.user_selector.invalid",
                "Provide exactly one existing staff account selector: email or userId.");
        }

        if (hasUserId)
        {
            return (request.UserId!.Value, null);
        }

        var staff = await staffDirectory.ResolveForMembershipCreationAsync(
            organizationId,
            request.Email,
            cancellationToken);
        return (staff.UserId, staff.Email);
    }

    private static MembershipApiResponse ToResponse(
        MembershipResult result,
        string? email) => new(
        result.Id,
        result.OrganizationId,
        result.UserId,
        email,
        result.Status,
        result.CreatedAtUtc,
        result.DisabledAtUtc);
}
