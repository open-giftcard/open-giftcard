using GiftCardPlatform.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record CreateUserApiRequest(string? Email, string? Password);

public sealed record LoginApiRequest(
    string? Email,
    string? Password,
    string? PhoneNumber = null);

public sealed record RefreshSessionApiRequest(string? RefreshToken);

public sealed record RevokeSessionApiRequest(string? RefreshToken);

public sealed record UserApiResponse(
    Guid Id,
    string? Email,
    string? PhoneNumber,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DisabledAtUtc);

public sealed record TokenPairApiResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

internal static class IdentityEndpoints
{
    public const string LoginRateLimitPolicy = "identity-login";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup($"{ApiRoutes.V1}/users")
            .WithTags("Identity")
            .RequireAuthorization();

        users.MapPost("/", CreateUserAsync)
            .WithName("CreateUser")
            .WithSummary("Creates a user account.")
            .Produces<UserApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapPost("/{id:guid}/disable", DisableUserAsync)
            .WithName("DisableUser")
            .WithSummary("Disables a user and revokes all active sessions.")
            .Produces<UserApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var authentication = app.MapGroup($"{ApiRoutes.V1}/auth")
            .WithTags("Authentication")
            .AllowAnonymous();

        authentication.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Exchanges an email or phone number and password for an access and refresh token.")
            .RequireRateLimiting(LoginRateLimitPolicy)
            .Produces<TokenPairApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        authentication.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshSession")
            .WithSummary("Rotates a refresh token and issues a new token pair.")
            .Produces<TokenPairApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        authentication.MapPost("/revoke", RevokeAsync)
            .WithName("RevokeSession")
            .WithSummary("Revokes the session identified by a refresh token.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> CreateUserAsync(
        [FromBody] CreateUserApiRequest request,
        IUserService userService,
        CancellationToken cancellationToken)
    {
        var result = await userService
            .CreateAsync(new CreateUserRequest(request.Email, request.Password), cancellationToken);
        var response = ToResponse(result);
        return Results.Created($"{ApiRoutes.V1}/users/{response.Id}", response);
    }

    private static async Task<IResult> DisableUserAsync(
        Guid id,
        IUserService userService,
        CancellationToken cancellationToken)
    {
        var result = await userService.DisableAsync(id, cancellationToken);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginApiRequest request,
        IAuthenticationService authenticationService,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService
            .LoginAsync(
                new LoginRequest(
                    SelectIdentifier(request.Email, request.PhoneNumber),
                    request.Password),
                cancellationToken);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody] RefreshSessionApiRequest request,
        IAuthenticationService authenticationService,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService
            .RefreshAsync(new RefreshSessionRequest(request.RefreshToken), cancellationToken);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> RevokeAsync(
        [FromBody] RevokeSessionApiRequest request,
        IAuthenticationService authenticationService,
        CancellationToken cancellationToken)
    {
        await authenticationService
            .RevokeAsync(new RevokeSessionRequest(request.RefreshToken), cancellationToken);
        return Results.NoContent();
    }

    private static UserApiResponse ToResponse(UserResult result) =>
        new(
            result.Id,
            result.Email,
            result.PhoneNumber,
            result.Status,
            result.CreatedAtUtc,
            result.DisabledAtUtc);

    private static TokenPairApiResponse ToResponse(TokenPairResult result) =>
        new(
            result.AccessToken,
            result.AccessTokenExpiresAtUtc,
            result.RefreshToken,
            result.RefreshTokenExpiresAtUtc);

    private static string? SelectIdentifier(string? email, string? phoneNumber)
    {
        if (!string.IsNullOrWhiteSpace(email) &&
            !string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(phoneNumber) ? phoneNumber : email;
    }
}
