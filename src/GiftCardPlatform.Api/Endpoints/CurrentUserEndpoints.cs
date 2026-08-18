using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record CurrentOrganizationApiResponse(
    Guid MembershipId,
    Guid TenantRootOrganizationId,
    OrganizationApiResponse Organization,
    IReadOnlyList<string> EffectivePermissions);

public sealed record CurrentUserApiResponse(
    Guid Id,
    string? Email,
    string? PhoneNumber,
    string Status,
    string ContextType,
    IReadOnlyList<string> PlatformPermissions,
    CurrentOrganizationApiResponse? OrganizationContext);

public sealed record UserOrganizationApiResponse(
    Guid MembershipId,
    Guid TenantRootOrganizationId,
    OrganizationApiResponse Organization,
    DateTimeOffset MembershipCreatedAtUtc);

internal static class CurrentUserEndpoints
{
    public static IEndpointRouteBuilder MapCurrentUserEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ApiRoutes.V1}/me")
            .WithTags("Current User")
            .RequireAuthorization();

        group.MapGet("/", GetCurrentUserAsync)
            .WithName("GetCurrentUser")
            .WithSummary("Returns the authenticated identity and selected authority context.")
            .WithDescription(
                "Without X-Organization-Id, returns identity or platform context. " +
                "With a verified organization header, returns that membership and its " +
                "effective permissions against the exact selected organization.")
            .Produces<CurrentUserApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/organizations", ListCurrentUserOrganizationsAsync)
            .WithName("ListCurrentUserOrganizations")
            .WithSummary("Lists organizations selectable by the authenticated user.")
            .WithDescription(
                "Exact-user, active-membership discovery. Call without X-Organization-Id; " +
                "the returned membership is not proof of authority until selected and verified.")
            .Produces<PagedApiResponse<UserOrganizationApiResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> GetCurrentUserAsync(
        IExecutionContext executionContext,
        IIdentityUserQuery userQuery,
        IOrganizationDiscoveryQuery organizationQuery,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsAuthenticated ||
            executionContext.UserId is not { } userId)
        {
            throw new UnauthorizedException(
                "auth.unauthenticated",
                "Authentication is required.");
        }

        var user = await userQuery
            .FindAsync(userId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedException(
                "auth.user_unavailable",
                "The authenticated user is unavailable.");

        CurrentOrganizationApiResponse? organizationContext = null;
        var contextType = executionContext.IsPlatformOperator ? "Platform" : "Identity";

        if (executionContext.ActiveMembershipId is not null)
        {
            var selected = await organizationQuery
                .GetSelectedOrganizationContextAsync(cancellationToken)
                .ConfigureAwait(false);
            organizationContext = new CurrentOrganizationApiResponse(
                selected.MembershipId,
                selected.TenantRootOrganizationId,
                OrganizationEndpoints.ToResponse(selected.Organization),
                selected.EffectivePermissions);
            contextType = "Organization";
        }

        return Results.Ok(new CurrentUserApiResponse(
            user.Id,
            user.Email,
            user.PhoneNumber,
            user.Status,
            contextType,
            executionContext.PlatformPermissions
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray(),
            organizationContext));
    }

    private static async Task<IResult> ListCurrentUserOrganizationsAsync(
        IOrganizationDiscoveryQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        int? offset = null)
    {
        var result = await query
            .ListCurrentUserOrganizationsAsync(
                new PageRequest(limit ?? PageRequest.DefaultLimit, offset ?? 0),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new PagedApiResponse<UserOrganizationApiResponse>(
            result.Items
                .Select(x => new UserOrganizationApiResponse(
                    x.MembershipId,
                    x.TenantRootOrganizationId,
                    OrganizationEndpoints.ToResponse(x.Organization),
                    x.MembershipCreatedAtUtc))
                .ToArray(),
            result.Limit,
            result.Offset,
            result.HasMore));
    }
}
