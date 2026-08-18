using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Domain;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using GiftCardPlatform.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Authorization.Application;

internal sealed class PlatformBootstrapService(
    AuthorizationDbContext dbContext,
    IIdentityBootstrapService identityBootstrapService,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOptions<PlatformBootstrapOptions> bootstrapOptions,
    TimeProvider timeProvider) : IPlatformBootstrapService
{
    internal const string AdministratorRoleName = "Platform Administrator";

    public async Task<PlatformAdministratorBootstrapResult> BootstrapAsync(
        BootstrapPlatformAdministratorRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var state = await dbContext.PlatformBootstrapStates
            .FromSqlInterpolated(
                $"select * from \"authorization\".platform_bootstrap_state where id = {PlatformBootstrapState.SingletonId} for update")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (state.IsCompleted)
        {
            throw new ConflictException(
                "bootstrap.unavailable",
                "Platform bootstrap is not available.");
        }

        ValidateSecret(request.Secret);

        await PermissionCatalogueSynchronizer
            .EnsureAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);

        var user = await identityBootstrapService
            .CreateInitialPlatformUserAsync(
                new CreateUserRequest(request.Email, request.Password),
                cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var role = PlatformRole.CreateSystem(AdministratorRoleName, now);
        foreach (var permission in PlatformPermissions.All)
        {
            role.Grant(permission);
        }

        var assignment = PlatformRoleAssignment.Create(user.Id, role.Id, now);
        dbContext.PlatformRoles.Add(role);
        dbContext.PlatformRoleAssignments.Add(assignment);
        state.Complete(user.Id, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                user.Id,
                AuditActorType.System,
                OrganizationScopeId: null,
                AuditOperations.PlatformAdministratorBootstrapped,
                nameof(PlatformRoleAssignment),
                assignment.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["user_id"] = user.Id.ToString(),
                    ["role_id"] = role.Id.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlatformAdministratorBootstrapResult(
            user.Id,
            user.Email!,
            role.Id,
            now);
    }

    private void ValidateSecret(string? supplied)
    {
        var configured = bootstrapOptions.Value.Secret;
        if (Encoding.UTF8.GetByteCount(configured) < 32)
        {
            throw new InvalidOperationException(
                $"{PlatformBootstrapOptions.SectionName}:Secret must contain at least 32 UTF-8 bytes before bootstrap.");
        }

        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 512)
        {
            throw InvalidSecret();
        }

        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash))
        {
            throw InvalidSecret();
        }
    }

    private static UnauthorizedException InvalidSecret() =>
        new("bootstrap.invalid", "Platform bootstrap could not be completed.");
}
