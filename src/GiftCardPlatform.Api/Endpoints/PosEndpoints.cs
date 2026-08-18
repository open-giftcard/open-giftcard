using GiftCardPlatform.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

internal static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost($"{ApiRoutes.V1}/pos/clients", RegisterClientAsync)
            .WithTags("POS")
            .WithName("RegisterPosClient")
            .WithSummary("Registers a point-of-sale integration.")
            .WithDescription(
                "Returns the client secret once. Only its hash is stored, so the secret " +
                "cannot be recovered afterwards and must be re-registered if lost.")
            .RequireAuthorization()
            .Produces<RegisteredPosClientResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet($"{ApiRoutes.V1}/pos/clients", GetClientsAsync)
            .WithTags("POS")
            .WithName("GetPosClients")
            .WithSummary("Lists registered point-of-sale integrations.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<PosClientResult>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost($"{ApiRoutes.V1}/pos/clients/{{posClientId:guid}}/terminals", RegisterTerminalAsync)
            .WithTags("POS")
            .WithName("RegisterPosTerminal")
            .WithSummary("Registers a till belonging to a point-of-sale integration.")
            .RequireAuthorization()
            .Produces<PosTerminalResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet($"{ApiRoutes.V1}/pos/clients/{{posClientId:guid}}/terminals", GetTerminalsAsync)
            .WithTags("POS")
            .WithName("GetPosTerminals")
            .WithSummary("Lists tills belonging to a point-of-sale integration.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<PosTerminalResult>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost($"{ApiRoutes.V1}/pos/auth/token", AuthenticateAsync)
            .WithTags("POS")
            .WithName("IssuePosAccessToken")
            .WithSummary("Exchanges POS client credentials and a terminal code for an access token.")
            .WithDescription(
                "Unknown, disabled, and wrong credentials are refused identically. The token " +
                "identifies the till only; it is not authority to charge any particular card, " +
                "which additionally requires a payment credential.")
            .AllowAnonymous()
            .Produces<PosAccessTokenResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> RegisterClientAsync(
        [FromBody] RegisterPosClientRequest request,
        IPosRegistrationService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        // The one-time secret must not be cached by any intermediary.
        response.Headers.CacheControl = "no-store";
        var created = await service.RegisterClientAsync(request, cancellationToken);
        return Results.Created($"{ApiRoutes.V1}/pos/clients/{created.Id}", created);
    }

    private static async Task<IResult> GetClientsAsync(
        IPosRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetClientsAsync(cancellationToken));

    private static async Task<IResult> RegisterTerminalAsync(
        Guid posClientId,
        [FromBody] RegisterPosTerminalRequest request,
        IPosRegistrationService service,
        CancellationToken cancellationToken)
    {
        var created = await service.RegisterTerminalAsync(
            posClientId,
            request,
            cancellationToken);
        return Results.Created(
            $"{ApiRoutes.V1}/pos/clients/{posClientId}/terminals/{created.Id}",
            created);
    }

    private static async Task<IResult> GetTerminalsAsync(
        Guid posClientId,
        IPosRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetTerminalsAsync(posClientId, cancellationToken));

    private static async Task<IResult> AuthenticateAsync(
        [FromBody] PosAccessTokenRequest request,
        IPosAuthenticationService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.AuthenticateAsync(request, cancellationToken));
    }
}
