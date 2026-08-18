using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

/// <summary>Explicit API request contract. Never an EF Core entity.</summary>
public sealed record CreateSubsidiaryApiRequest
{
    /// <example>Example Customer Retail</example>
    public string? Name { get; init; }

    /// <example>EXAMPLE-RETAIL</example>
    public string? Code { get; init; }
}

/// <summary>Explicit API response contract.</summary>
public sealed record SubsidiaryApiResponse(
    Guid Id,
    Guid ParentOrganizationId,
    string Name,
    string Code,
    string Status,
    int Depth,
    DateTimeOffset CreatedAtUtc);

internal static class SubsidiaryEndpoints
{
    public static IEndpointRouteBuilder MapSubsidiaryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/subsidiaries")
            .WithTags("Subsidiaries")
            .RequireAuthorization();

        group.MapPost("/", CreateAsync)
            .WithName("CreateSubsidiary")
            .WithSummary("Creates a subsidiary beneath the caller's organization.")
            .WithDescription(
                "Requires the organization.create_subsidiary permission. The parent is the caller's " +
                "active organization, not a request-body value. Enforces the configured maximum " +
                "hierarchy depth. The subsidiary and its append-only audit record are committed atomically.")
            .Produces<SubsidiaryApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithName("ListSubsidiaries")
            .WithSummary("Lists the direct subsidiaries of the caller's organization.")
            .WithDescription(
                "Requires the organization.view permission. Paged via limit (1-200, default 50) and offset.")
            .Produces<PagedApiResponse<SubsidiaryApiResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        [FromBody] CreateSubsidiaryApiRequest request,
        ISubsidiaryService subsidiaryService,
        CancellationToken cancellationToken)
    {
        var result = await subsidiaryService.CreateSubsidiaryAsync(
            organizationId,
            new CreateSubsidiaryRequest(request.Name, request.Code),
            cancellationToken);

        var response = ToResponse(result);
        return Results.Created($"{ApiRoutes.V1}/organizations/{response.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ISubsidiaryService subsidiaryService,
        CancellationToken cancellationToken,
        int? limit = null,
        int? offset = null)
    {
        var page = new PageRequest(limit ?? PageRequest.DefaultLimit, offset ?? 0);
        var results = await subsidiaryService.ListSubsidiariesAsync(organizationId, page, cancellationToken);

        return Results.Ok(new PagedApiResponse<SubsidiaryApiResponse>(
            [.. results.Items.Select(ToResponse)],
            results.Limit,
            results.Offset,
            results.HasMore));
    }

    private static SubsidiaryApiResponse ToResponse(SubsidiaryResult result) => new(
        result.Id,
        result.ParentOrganizationId,
        result.Name,
        result.Code,
        result.Status,
        result.Depth,
        result.CreatedAtUtc);
}
