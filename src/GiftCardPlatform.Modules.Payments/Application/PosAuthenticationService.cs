using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Payments.Domain;
using GiftCardPlatform.Modules.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GiftCardPlatform.Modules.Payments.Application;

internal sealed class PosAuthenticationService(
    PaymentsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    TimeProvider timeProvider,
    IOptions<PosAuthenticationOptions> posOptions,
    IOptions<PosTokenSigningOptions> signingOptions)
    : IPosAuthenticationService, IPosPrincipalResolver
{
    private readonly PosAuthenticationOptions settings = posOptions.Value;
    private readonly PosTokenSigningOptions signing = signingOptions.Value;

    public async Task<PosAccessTokenResult> AuthenticateAsync(
        PosAccessTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Normalization must not leak which field was wrong, so malformed input
        // is refused with the same error as a wrong secret.
        string clientCode;
        string terminalCode;
        try
        {
            clientCode = PosClient.NormalizeCode(request.ClientCode);
            terminalCode = PosClient.NormalizeCode(request.TerminalCode);
        }
        catch (ValidationFailedException)
        {
            throw InvalidCredentials();
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var client = await dbContext.PosClients
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == clientCode, cancellationToken)
            .ConfigureAwait(false);
        var terminal = client is null
            ? null
            : await dbContext.PosTerminals
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.PosClientId == client.Id && item.Code == terminalCode,
                    cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The secret is verified even when the client is unknown, so an
        // attacker cannot distinguish "no such client" from "wrong secret" by
        // response timing. Unknown, disabled, and wrong-secret all return the
        // same refusal.
        var secretMatches = PosCredentialCodec.Matches(
            client?.SecretHash ?? new string('0', PosCredentialCodec.HashHexLength),
            request.ClientSecret);
        if (client is null || terminal is null || !secretMatches ||
            !client.IsUsable || !terminal.IsUsable)
        {
            throw InvalidCredentials();
        }

        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signing.SigningKey));
        var token = new JwtSecurityToken(
            issuer: signing.Issuer,
            audience: signing.Audience,
            claims:
            [
                new Claim(PosTokenClaims.Principal, PosTokenClaims.PrincipalValue),
                new Claim(PosTokenClaims.ClientId, client.Id.ToString()),
                new Claim(PosTokenClaims.TerminalId, terminal.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7(now).ToString()),
            ],
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new PosAccessTokenResult(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            client.Id,
            terminal.Id,
            terminal.StoreReference);
    }

    private static UnauthorizedException InvalidCredentials() =>
        new("pos.credentials.invalid", "The POS credentials are not valid.");

    public async Task<PosPrincipal?> ResolveAsync(
        Guid posClientId,
        Guid posTerminalId,
        CancellationToken cancellationToken)
    {
        if (posClientId == Guid.Empty || posTerminalId == Guid.Empty)
        {
            return null;
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var active = await (
                from client in dbContext.PosClients.AsNoTracking()
                join terminal in dbContext.PosTerminals.AsNoTracking()
                    on client.Id equals terminal.PosClientId
                where client.Id == posClientId &&
                    terminal.Id == posTerminalId &&
                    client.Status == PosClientStatus.Active &&
                    terminal.Status == PosTerminalStatus.Active
                select new PosPrincipal(client.Id, terminal.Id))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }
}

/// <summary>
/// The JWT signing material shared with user authentication. Payments binds the
/// same configuration section rather than depending on the Identity module, so
/// no module boundary is crossed to sign a device token (ADR-004).
///
/// The issuer and audience defaults must match the ones user authentication
/// falls back to. Neither is set in configuration today, so leaving these empty
/// mints a device token the API then rejects as having no audience.
/// </summary>
internal sealed class PosTokenSigningOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; set; } = "GiftCardPlatform";

    public string Audience { get; set; } = "GiftCardPlatform.Api";

    public string SigningKey { get; set; } = string.Empty;
}
