using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

/// <summary>Explicit API request contract. Never an EF Core entity.</summary>
public sealed record CreateOrganizationApiRequest
{
    /// <example>Example Customer Company</example>
    public string? Name { get; init; }

    /// <example>EXAMPLE</example>
    public string? Code { get; init; }
}

/// <summary>Explicit API response contract.</summary>
public sealed record OrganizationApiResponse(
    Guid Id,
    string Name,
    string Code,
    string Status,
    int Depth,
    DateTimeOffset CreatedAtUtc);

internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ApiRoutes.V1}/organizations")
            .WithTags("Organizations")
            .RequireAuthorization();

        group.MapPost("/", CreateAsync)
            .WithName("CreateOrganization")
            .WithSummary("Creates a root customer organization.")
            .WithDescription(
                "Requires the platform.organizations.create permission. The organization and its " +
                "append-only audit record are committed atomically.")
            .Produces<OrganizationApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithName("ListPlatformOrganizations")
            .WithSummary("Lists root customer organizations for a platform operator.")
            .WithDescription(
                "Requires platform.organizations.view. Supports bounded offset paging, " +
                "literal case-insensitive name/code search, and exact status filtering.")
            .Produces<PagedApiResponse<OrganizationApiResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetOrganization")
            .WithSummary("Reads a single organization.")
            .WithDescription("Requires the platform.organizations.view permission.")
            .Produces<OrganizationApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateOrganizationApiRequest request,
        IOrganizationService organizationService,
        CancellationToken cancellationToken)
    {
        var result = await organizationService.CreateRootOrganizationAsync(
            new CreateRootOrganizationRequest(request.Name, request.Code),
            cancellationToken);

        var response = ToResponse(result);
        return Results.Created($"{ApiRoutes.V1}/organizations/{response.Id}", response);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IOrganizationService organizationService,
        CancellationToken cancellationToken)
    {
        var result = await organizationService.GetOrganizationAsync(id, cancellationToken);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> ListAsync(
        IOrganizationDiscoveryQuery query,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        int? limit = null,
        int? offset = null)
    {
        var result = await query
            .ListPlatformOrganizationsAsync(
                new OrganizationListRequest(
                    search,
                    status,
                    new PageRequest(limit ?? PageRequest.DefaultLimit, offset ?? 0)),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new PagedApiResponse<OrganizationApiResponse>(
            result.Items.Select(ToResponse).ToArray(),
            result.Limit,
            result.Offset,
            result.HasMore));
    }

    internal static OrganizationApiResponse ToResponse(OrganizationResult result) => new(
        result.Id,
        result.Name,
        result.Code,
        result.Status,
        result.Depth,
        result.CreatedAtUtc);
}
