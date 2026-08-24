using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Payments.Domain;
using GiftCardPlatform.Modules.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Payments.Application;

internal sealed class PosRegistrationService(
    PaymentsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    TimeProvider timeProvider) : IPosRegistrationService
{
    public async Task<RegisteredPosClientResult> RegisterClientAsync(
        RegisterPosClientRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePlatformPermission();

        var code = PosClient.NormalizeCode(request.Code);
        var secret = PosCredentialCodec.Create();
        var now = timeProvider.GetUtcNow();
        var client = PosClient.Register(
            Guid.CreateVersion7(now),
            code,
            request.DisplayName,
            secret.Hash,
            now);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        if (await dbContext.PosClients
                .AnyAsync(existing => existing.Code == code, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ConflictException(
                "pos.client.code.duplicate",
                "A POS client with that code already exists.");
        }

        dbContext.PosClients.Add(client);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The secret is never audited (DOMAIN_RULES §6.5).
        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                null,
                "pos.client.registered",
                nameof(PosClient),
                client.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["code"] = client.Code }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RegisteredPosClientResult(
            client.Id,
            client.Code,
            client.DisplayName,
            secret.Secret,
            client.RegisteredAtUtc);
    }

    public async Task<IReadOnlyList<PosClientResult>> GetClientsAsync(
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var clients = await dbContext.PosClients
            .AsNoTracking()
            .OrderBy(client => client.Code)
            .Select(client => new PosClientResult(
                client.Id,
                client.Code,
                client.DisplayName,
                client.Status.ToString(),
                client.RegisteredAtUtc,
                client.DisabledAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return clients;
    }

    public async Task<PosClientResult> DisableClientAsync(
        Guid posClientId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var client = await dbContext.PosClients
            .SingleOrDefaultAsync(item => item.Id == posClientId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "pos.client.not_found",
                "The POS client was not found.");
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
                null,
                "pos.client.disabled",
                nameof(PosClient),
                client.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["code"] = client.Code }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(client);
    }

    public async Task<PosTerminalResult> RegisterTerminalAsync(
        Guid posClientId,
        RegisterPosTerminalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePlatformPermission();

        var now = timeProvider.GetUtcNow();
        var terminal = PosTerminal.Register(
            Guid.CreateVersion7(now),
            posClientId,
            request.Code,
            request.StoreReference,
            now);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var client = await dbContext.PosClients
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == posClientId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "pos.client.not_found",
                "The POS client was not found.");
        if (!client.IsUsable)
        {
            throw new ConflictException(
                "pos.client.disabled",
                "A disabled POS client cannot register terminals.");
        }

        if (await dbContext.PosTerminals
                .AnyAsync(
                    existing => existing.PosClientId == posClientId &&
                        existing.Code == terminal.Code,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ConflictException(
                "pos.terminal.code.duplicate",
                "That terminal code already exists for this POS client.");
        }

        dbContext.PosTerminals.Add(terminal);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                null,
                "pos.terminal.registered",
                nameof(PosTerminal),
                terminal.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["posClientId"] = posClientId.ToString(),
                    ["code"] = terminal.Code,
                    ["storeReference"] = terminal.StoreReference,
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(terminal);
    }

    public async Task<IReadOnlyList<PosTerminalResult>> GetTerminalsAsync(
        Guid posClientId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var terminals = await dbContext.PosTerminals
            .AsNoTracking()
            .Where(terminal => terminal.PosClientId == posClientId)
            .OrderBy(terminal => terminal.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return terminals.Select(ToResult).ToList();
    }

    public async Task<PosTerminalResult> DisableTerminalAsync(
        Guid posClientId,
        Guid posTerminalId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var terminal = await dbContext.PosTerminals
            .SingleOrDefaultAsync(
                item => item.Id == posTerminalId && item.PosClientId == posClientId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "pos.terminal.not_found",
                "The POS terminal was not found.");
        if (!terminal.IsUsable)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(terminal);
        }

        terminal.Disable(now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                null,
                "pos.terminal.disabled",
                nameof(PosTerminal),
                terminal.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["posClientId"] = posClientId.ToString(),
                    ["code"] = terminal.Code,
                    ["storeReference"] = terminal.StoreReference,
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(terminal);
    }

    private static PosClientResult ToResult(PosClient client) =>
        new(
            client.Id,
            client.Code,
            client.DisplayName,
            client.Status.ToString(),
            client.RegisteredAtUtc,
            client.DisabledAtUtc);

    private static PosTerminalResult ToResult(PosTerminal terminal) =>
        new(
            terminal.Id,
            terminal.PosClientId,
            terminal.Code,
            terminal.StoreReference,
            terminal.Status.ToString(),
            terminal.RegisteredAtUtc,
            terminal.DisabledAtUtc);

    private void RequirePlatformPermission()
    {
        if (!executionContext.HasPlatformPermission(PlatformPermissions.PosClientsManage) ||
            executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "pos.clients.manage.denied",
                "Managing POS clients requires the platform POS administration permission.");
        }
    }
}
