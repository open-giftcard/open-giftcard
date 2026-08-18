using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record BootstrapPlatformAdministratorApiRequest(
    string? Email,
    string? Password);

public sealed record PlatformAdministratorBootstrapApiResponse(
    Guid UserId,
    string Email,
    Guid PlatformRoleId,
    DateTimeOffset CompletedAtUtc);

public sealed record AssignInitialOrganizationAdministratorApiRequest(Guid UserId);

public sealed record InitialOrganizationAdministratorApiResponse(
    Guid OrganizationId,
    Guid UserId,
    Guid MembershipId,
    Guid RoleId,
    Guid RoleAssignmentId,
    DateTimeOffset AssignedAtUtc);

internal static class BootstrapEndpoints
{
    public const string BootstrapSecretHeader = "X-Platform-Bootstrap-Secret";
    public const string RateLimitPolicy = "platform-bootstrap";

    public static IEndpointRouteBuilder MapBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                $"{ApiRoutes.V1}/bootstrap/platform-administrator",
                BootstrapPlatformAdministratorAsync)
            .WithTags("Bootstrap")
            .WithName("BootstrapPlatformAdministrator")
            .WithSummary("Creates the first platform administrator exactly once.")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<PlatformAdministratorBootstrapApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapPost(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/initial-administrator",
                AssignInitialOrganizationAdministratorAsync)
            .WithTags("Bootstrap")
            .WithName("AssignInitialOrganizationAdministrator")
            .WithSummary("Assigns the first Company Administrator to a root organization.")
            .RequireAuthorization()
            .Produces<InitialOrganizationAdministratorApiResponse>(
                StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> BootstrapPlatformAdministratorAsync(
        [FromHeader(Name = BootstrapSecretHeader)] string? secret,
        [FromBody] BootstrapPlatformAdministratorApiRequest request,
        IPlatformBootstrapService bootstrapService,
        CancellationToken cancellationToken)
    {
        var result = await bootstrapService.BootstrapAsync(
            new BootstrapPlatformAdministratorRequest(
                secret,
                request.Email,
                request.Password),
            cancellationToken);

        return Results.Created(
            $"{ApiRoutes.V1}/users/{result.UserId}",
            new PlatformAdministratorBootstrapApiResponse(
                result.UserId,
                result.Email!,
                result.PlatformRoleId,
                result.CompletedAtUtc));
    }

    private static async Task<IResult> AssignInitialOrganizationAdministratorAsync(
        Guid organizationId,
        [FromBody] AssignInitialOrganizationAdministratorApiRequest request,
        IInitialOrganizationAdministratorService administratorService,
        CancellationToken cancellationToken)
    {
        var result = await administratorService
            .AssignAsync(organizationId, request.UserId, cancellationToken);
        return Results.Created(
            $"{ApiRoutes.V1}/organizations/{organizationId}/memberships/{result.MembershipId}",
            new InitialOrganizationAdministratorApiResponse(
                result.OrganizationId,
                result.UserId,
                result.MembershipId,
                result.RoleId,
                result.RoleAssignmentId,
                result.AssignedAtUtc));
    }
}
