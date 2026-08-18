using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using GiftCardPlatform.Modules.Partners.Domain;
using GiftCardPlatform.Modules.Partners.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Partners.Application;

internal sealed class PartnerRegistrationService(
    PartnersDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOrganizationFinancialEligibilityQuery organizationEligibility,
    IAuditRecorder auditRecorder,
    TimeProvider timeProvider) : IPartnerRegistrationService
{
    public async Task<PartnerResult> RegisterAsync(
        RegisterPartnerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePlatformPermission();

        var code = Partner.NormalizeCode(request.Code);
        var now = timeProvider.GetUtcNow();
        var partner = Partner.Register(
            Guid.CreateVersion7(now),
            request.RootOrganizationId,
            code,
            request.DisplayName,
            now);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // The funding tenant must be an active root. A subsidiary would put the
        // partner's minting on a different organization's corporate credit than
        // the one RLS isolates it to, and an inactive root cannot be funded.
        if (!await organizationEligibility
                .IsActiveRootAsync(request.RootOrganizationId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ValidationFailedException(
                "partner.root_organization.invalid",
                "A partner must be anchored to an active root organization.");
        }

        if (await dbContext.Partners
                .AnyAsync(existing => existing.Code == code, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ConflictException(
                "partner.code.duplicate",
                "A partner with that code already exists.");
        }

        if (await dbContext.Partners
                .AnyAsync(
                    existing => existing.RootOrganizationId == request.RootOrganizationId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ConflictException(
                "partner.root_organization.duplicate",
                "That organization is already registered as a partner.");
        }

        dbContext.Partners.Add(partner);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                partner.RootOrganizationId,
                "partner.registered",
                nameof(Partner),
                partner.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["code"] = partner.Code,
                    ["rootOrganizationId"] = partner.RootOrganizationId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(partner);
    }

    public async Task<IReadOnlyList<PartnerResult>> GetPartnersAsync(
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var partners = await dbContext.Partners
            .AsNoTracking()
            .OrderBy(partner => partner.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return partners.Select(ToResult).ToList();
    }

    public async Task<RegisteredPartnerApiClientResult> RegisterClientAsync(
        Guid partnerId,
        RegisterPartnerApiClientRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePlatformPermission();

        var code = PartnerApiClient.NormalizeCode(request.Code);
        var secret = PartnerCredentialCodec.Create();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var partner = await dbContext.Partners
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == partnerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("partner.not_found", "The partner was not found.");

        if (!partner.IsUsable)
        {
            throw new ConflictException(
                "partner.disabled",
                "A disabled partner cannot register API clients.");
        }

        if (await dbContext.ApiClients
                .AnyAsync(existing => existing.Code == code, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ConflictException(
                "partner.api_client.code.duplicate",
                "A partner API client with that code already exists.");
        }

        var client = PartnerApiClient.Register(
            Guid.CreateVersion7(now),
            partner.Id,
            partner.RootOrganizationId,
            code,
            request.DisplayName,
            // Minting is the only capability today, so an unspecified scope set
            // means the key the caller obviously wants rather than a dead one.
            request.Scopes ?? [PartnerScopes.GiftCardsMint],
            secret.Hash,
            now);

        dbContext.ApiClients.Add(client);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The secret is never audited (DOMAIN_RULES §6.5).
        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                partner.RootOrganizationId,
                "partner.api_client.registered",
                nameof(PartnerApiClient),
                client.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["partnerId"] = partner.Id.ToString(),
                    ["code"] = client.Code,
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The only time the raw secret exists outside the caller's own storage.
        return new RegisteredPartnerApiClientResult(ToResult(client), secret.Secret);
    }

    public async Task<IReadOnlyList<PartnerApiClientResult>> GetClientsAsync(
        Guid partnerId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var clients = await dbContext.ApiClients
            .AsNoTracking()
            .Where(client => client.PartnerId == partnerId)
            .OrderBy(client => client.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return clients.Select(ToResult).ToList();
    }

    public async Task<PartnerApiClientResult> DisableClientAsync(
        Guid partnerId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var client = await dbContext.ApiClients
            .SingleOrDefaultAsync(
                item => item.Id == clientId && item.PartnerId == partnerId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "partner.api_client.not_found",
                "The partner API client was not found.");

        // Disabling an already-disabled client changes nothing, so it must not
        // append an audit record saying it did. The audit store is append-only
        // by privilege, so a false entry could never be corrected, and a retry
        // or a double click would leave two disable events for one key with no
        // way to tell which one took effect.
        if (!client.IsUsable)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(client);
        }

        client.Disable(now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                client.RootOrganizationId,
                "partner.api_client.disabled",
                nameof(PartnerApiClient),
                client.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["partnerId"] = partnerId.ToString() }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(client);
    }

    public async Task<PartnerResult> DisablePartnerAsync(
        Guid partnerId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var partner = await dbContext.Partners
            .SingleOrDefaultAsync(item => item.Id == partnerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("partner.not_found", "The partner was not found.");

        // Same reasoning as DisableClientAsync: a no-op must not be audited.
        if (!partner.IsUsable)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(partner);
        }

        partner.Disable(now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                partner.RootOrganizationId,
                "partner.disabled",
                nameof(Partner),
                partner.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["code"] = partner.Code }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(partner);
    }

    private static PartnerResult ToResult(Partner partner) =>
        new(
            partner.Id,
            partner.RootOrganizationId,
            partner.Code,
            partner.DisplayName,
            partner.Status,
            partner.RegisteredAtUtc,
            partner.DisabledAtUtc);

    private static PartnerApiClientResult ToResult(PartnerApiClient client) =>
        new(
            client.Id,
            client.PartnerId,
            client.Code,
            client.DisplayName,
            client.Scopes,
            client.Status,
            client.RegisteredAtUtc,
            client.DisabledAtUtc);

    private void RequirePlatformPermission()
    {
        if (!executionContext.HasPlatformPermission(PlatformPermissions.PartnersManage) ||
            executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "partner.manage.denied",
                "The required permission is missing.");
        }
    }
}
