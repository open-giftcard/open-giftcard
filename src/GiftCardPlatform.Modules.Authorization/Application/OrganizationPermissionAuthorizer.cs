using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;

namespace GiftCardPlatform.Modules.Authorization.Application;

/// <summary>
/// Converts the active membership and evaluator into the single authorization
/// guard consumed by application services.
/// </summary>
internal sealed class OrganizationPermissionAuthorizer(
    IExecutionContext executionContext,
    IPermissionEvaluator permissionEvaluator) : IOrganizationPermissionAuthorizer
{
    public async Task RequirePermissionAsync(
        Guid targetOrganizationId,
        string permission,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        if (!executionContext.IsAuthenticated ||
            executionContext.IsPlatformOperator ||
            executionContext.UserId is null ||
            executionContext.ActiveMembershipId is null ||
            executionContext.ActiveOrganizationId is null)
        {
            throw new ForbiddenException("auth.unauthenticated", "Authentication is required.");
        }

        var hasPermission = await permissionEvaluator
            .HasPermissionAsync(
                executionContext.ActiveMembershipId.Value,
                targetOrganizationId,
                permission,
                cancellationToken)
            .ConfigureAwait(false);

        if (!hasPermission)
        {
            // Deliberately does not reveal whether the target organization exists.
            throw new ForbiddenException("auth.forbidden", "The required permission is missing.");
        }
    }
}
