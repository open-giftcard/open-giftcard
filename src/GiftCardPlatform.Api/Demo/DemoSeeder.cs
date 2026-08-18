using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Api.Demo;

/// <summary>
/// Builds the demonstration tenant by driving the ordinary application services.
///
/// Nothing here reaches around a rule. Platform-scoped work runs as a trusted
/// system actor holding an explicit, narrow permission set, the same shape every
/// background worker already uses. Tenant-scoped work runs as the seeded company
/// administrator's own membership, because that is who would really do it and
/// because it exercises the organization authorization path rather than
/// stepping over it. A seed that bypassed authorization would prove nothing
/// about the system and would be the back door this feature must not become.
/// </summary>
internal sealed class DemoSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DemoSeedOptions> options,
    IOptions<PlatformBootstrapOptions> bootstrapOptions,
    ILogger<DemoSeeder> logger)
{
    private readonly DemoSeedOptions _options = options.Value;

    // The seed does not carry its own copy of the bootstrap secret. It reads the
    // one the platform is already configured with, so there is no second value
    // to keep in step and no way to seed a database whose bootstrap secret the
    // operator did not supply.
    private readonly string _bootstrapSecret = bootstrapOptions.Value.Secret;

    private static readonly Action<ILogger, string, Exception?> SecretMissing =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1950, nameof(SecretMissing)),
            "Demo seed is enabled but {Section}:Secret is not configured. The seed creates the " +
            "platform administrator through the ordinary guarded bootstrap and will not run " +
            "without it.");

    private static readonly Action<ILogger, string, Exception?> AlreadySeeded =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1951, nameof(AlreadySeeded)),
            "Demo seed already applied: a root organization with code {Code} exists.");

    private static readonly Action<ILogger, string, Exception?> AdministratorCreated =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1952, nameof(AdministratorCreated)),
            "Bootstrapped platform administrator {Email}.");

    private static readonly Action<ILogger, Exception?> BootstrapAlreadyDone =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1953, nameof(BootstrapAlreadyDone)),
            "Platform bootstrap has already completed for this database; the demo seed left the " +
            "existing administrator alone.");

    private static readonly Action<ILogger, string, Guid, string, string, Exception?> SeedApplied =
        LoggerMessage.Define<string, Guid, string, string>(
            LogLevel.Information,
            new EventId(1954, nameof(SeedApplied)),
            "Demo seed applied. Organization {Code} ({OrganizationId}); platform administrator " +
            "{PlatformEmail}; company administrator {CompanyEmail}.");

    /// <summary>
    /// Attribution for platform-scoped seed work. Declared here rather than in
    /// the shared SystemActorIds on purpose: this actor exists only in
    /// Development, and the production list should not carry a demo concern.
    /// </summary>
    private static readonly Guid SeedActorId =
        Guid.Parse("019c0598-6700-7000-8000-000000000090");

    /// <summary>Exactly the platform permissions the seed needs. Nothing wider.</summary>
    private static readonly string[] SeedPlatformPermissions =
    [
        PlatformPermissions.OrganizationsCreate,
        PlatformPermissions.OrganizationsView,
        PlatformPermissions.UsersCreate,
        PlatformPermissions.InitialAdministratorsAssign,
        PlatformPermissions.CorporateCreditsAllocate,
        PlatformPermissions.CorporateCreditsView,
        PlatformPermissions.PosClientsManage,
    ];

    public async Task<DemoSeedOutcome> SeedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_bootstrapSecret))
        {
            SecretMissing(logger, PlatformBootstrapOptions.SectionName, null);
            return DemoSeedOutcome.Skipped;
        }

        // Idempotency key. The organization code is unique platform-wide, so its
        // presence means a previous run got at least as far as creating it.
        if (await DemoOrganizationExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            AlreadySeeded(logger, _options.OrganizationCode, null);
            return DemoSeedOutcome.AlreadyApplied;
        }

        await EnsurePlatformAdministratorAsync(cancellationToken).ConfigureAwait(false);

        var organization = await CreateOrganizationAsync(cancellationToken).ConfigureAwait(false);

        var companyAdministrator = await AssignCompanyAdministratorAsync(
            organization.Id,
            cancellationToken).ConfigureAwait(false);

        await CreateSubsidiariesAsync(companyAdministrator, cancellationToken)
            .ConfigureAwait(false);

        await AllocateCorporateCreditAsync(organization.Id, cancellationToken)
            .ConfigureAwait(false);

        var giftCard = await IssueGiftCardAsync(companyAdministrator, cancellationToken)
            .ConfigureAwait(false);

        var invitation = await DistributeAsync(companyAdministrator, giftCard.Id, cancellationToken)
            .ConfigureAwait(false);

        var recipientUserId = await ClaimAsync(
            companyAdministrator,
            invitation.Id,
            cancellationToken).ConfigureAwait(false);

        var till = await RegisterTillAsync(cancellationToken).ConfigureAwait(false);

        await TakePaymentAndRefundAsync(
            recipientUserId,
            giftCard.Id,
            till,
            cancellationToken).ConfigureAwait(false);

        SeedApplied(
            logger,
            _options.OrganizationCode,
            organization.Id,
            _options.PlatformAdministratorEmail,
            _options.CompanyAdministratorEmail,
            null);

        return DemoSeedOutcome.Applied;
    }

    /// <summary>
    /// Creates the first platform administrator through the ordinary one-shot
    /// bootstrap, so the published demo credentials are a real account.
    ///
    /// Bootstrap succeeds exactly once per database. A second run is therefore a
    /// conflict rather than a failure, and the seed carries on: it does not need
    /// the administrator's identifier, because its own platform work runs as the
    /// system actor.
    /// </summary>
    private async Task EnsurePlatformAdministratorAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        // Bootstrap authorizes on the secret alone, by design: no authority
        // exists yet for it to authorize against.
        Context(scope).SetAnonymous();

        try
        {
            await scope.ServiceProvider
                .GetRequiredService<IPlatformBootstrapService>()
                .BootstrapAsync(
                    new BootstrapPlatformAdministratorRequest(
                        _bootstrapSecret,
                        _options.PlatformAdministratorEmail,
                        _options.Password),
                    cancellationToken)
                .ConfigureAwait(false);

            AdministratorCreated(logger, _options.PlatformAdministratorEmail, null);
        }
        catch (ConflictException)
        {
            BootstrapAlreadyDone(logger, null);
        }
    }

    private async Task<bool> DemoOrganizationExistsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsSeedActor(scope);

        var page = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDiscoveryQuery>()
            .ListPlatformOrganizationsAsync(
                new OrganizationListRequest(_options.OrganizationCode, null, new PageRequest(50, 0)),
                cancellationToken)
            .ConfigureAwait(false);

        return page.Items.Any(organization =>
            string.Equals(
                organization.Code,
                _options.OrganizationCode,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<OrganizationResult> CreateOrganizationAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsSeedActor(scope);

        return await scope.ServiceProvider
            .GetRequiredService<IOrganizationService>()
            .CreateRootOrganizationAsync(
                new CreateRootOrganizationRequest(
                    _options.OrganizationName,
                    _options.OrganizationCode),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OrganizationActor> AssignCompanyAdministratorAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsSeedActor(scope);

        var user = await scope.ServiceProvider
            .GetRequiredService<IUserService>()
            .CreateAsync(
                new CreateUserRequest(_options.CompanyAdministratorEmail, _options.Password),
                cancellationToken)
            .ConfigureAwait(false);

        var assignment = await scope.ServiceProvider
            .GetRequiredService<IInitialOrganizationAdministratorService>()
            .AssignAsync(organizationId, user.Id, cancellationToken)
            .ConfigureAwait(false);

        return new OrganizationActor(
            assignment.UserId,
            assignment.MembershipId,
            organizationId);
    }

    /// <summary>
    /// Creates the two child organizations as the company administrator.
    ///
    /// Subsidiary creation requires <c>organization.create_subsidiary</c> and
    /// insists the parent is the caller's own active organization, so this step
    /// cannot be done with platform authority. Running it as the seeded
    /// administrator is both the honest journey and a live check that the
    /// membership the seed just created actually works.
    /// </summary>
    private async Task CreateSubsidiariesAsync(
        OrganizationActor companyAdministrator,
        CancellationToken cancellationToken)
    {
        var children = new[]
        {
            (Name: _options.FirstSubsidiaryName, Code: _options.FirstSubsidiaryCode),
            (Name: _options.SecondSubsidiaryName, Code: _options.SecondSubsidiaryCode),
        };

        foreach (var child in children)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            Context(scope).SetOrganizationMember(
                companyAdministrator.UserId,
                companyAdministrator.MembershipId,
                companyAdministrator.OrganizationId,
                companyAdministrator.OrganizationId);

            await scope.ServiceProvider
                .GetRequiredService<ISubsidiaryService>()
                .CreateSubsidiaryAsync(
                    companyAdministrator.OrganizationId,
                    new CreateSubsidiaryRequest(child.Name, child.Code),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Allocates the organization's corporate credit. This is the first ledger
    /// posting in the demonstration: value moves from the platform funding
    /// account into the customer's corporate credit, balanced, in one
    /// transaction.
    /// </summary>
    private async Task AllocateCorporateCreditAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsSeedActor(scope);

        await scope.ServiceProvider
            .GetRequiredService<ICorporateCreditAllocationService>()
            .AllocateAsync(
                new AllocateCorporateCreditRequest(
                    organizationId,
                    _options.FundingAmount,
                    _options.Currency,
                    "DEMO-FUNDING-001",
                    IdempotencyKey("funding")),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Issues one card from the organization's inventory, debiting the corporate
    /// credit the previous step posted.
    /// </summary>
    private async Task<GiftCardResult> IssueGiftCardAsync(
        OrganizationActor companyAdministrator,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsCompanyAdministrator(scope, companyAdministrator);

        var now = TimeProvider.System.GetUtcNow();

        return await scope.ServiceProvider
            .GetRequiredService<IGiftCardIssuanceService>()
            .IssueAsync(
                companyAdministrator.OrganizationId,
                new IssueGiftCardRequest(
                    _options.GiftCardAmount,
                    _options.Currency,
                    now,
                    now.AddYears(1),
                    IsTransferable: true,
                    IsDivisible: true,
                    "DEMO-ISSUANCE-001",
                    IdempotencyKey("issuance")),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DistributionInvitationResult> DistributeAsync(
        OrganizationActor companyAdministrator,
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsCompanyAdministrator(scope, companyAdministrator);

        return await scope.ServiceProvider
            .GetRequiredService<IGiftCardDistributionService>()
            .DistributeAsync(
                companyAdministrator.OrganizationId,
                new DistributeGiftCardRequest(
                    giftCardId,
                    RecipientContactType.Email,
                    _options.RecipientEmail,
                    "DEMO-DISTRIBUTION-001",
                    IdempotencyKey("distribution")),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Claims the card as the recipient, which creates their identity.
    ///
    /// The raw claim token is never persisted, so the seed reads it back from the
    /// Development delivery sink, which is where a developer looks for it when no
    /// mail provider is configured. That keeps the seed on the same path a real
    /// recipient takes rather than minting a token behind the service.
    /// </summary>
    private async Task<Guid> ClaimAsync(
        OrganizationActor companyAdministrator,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        string claimUrl;

        await using (var lookupScope = scopeFactory.CreateAsyncScope())
        {
            // Reading the captured activation link is an organization-scoped
            // operation gated on organization.gift_cards.distribute, so it runs
            // as the company administrator rather than with platform authority.
            AsCompanyAdministrator(lookupScope, companyAdministrator);

            var delivery = await lookupScope.ServiceProvider
                .GetRequiredService<IDevelopmentClaimDeliveryQuery>()
                .FindAsync(companyAdministrator.OrganizationId, invitationId, cancellationToken)
                .ConfigureAwait(false);

            if (delivery is null)
            {
                throw new InvalidOperationException(
                    "The activation link for the seeded invitation was not captured. The demo " +
                    "seed reads it from the Development delivery sink, which is populated by the " +
                    "notification dispatcher. Check that Notifications:DispatchEnabled is on and " +
                    "that no real SMTP sender is configured, so links are captured rather than " +
                    "sent.");
            }

            claimUrl = delivery.ClaimUrl;
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        // The claim service establishes its own claim-candidate context from the
        // token, so the caller starts anonymous, as a recipient would.
        Context(scope).SetAnonymous();

        var result = await scope.ServiceProvider
            .GetRequiredService<IGiftCardClaimService>()
            .ClaimAsync(
                new ClaimGiftCardRequest(
                    ClaimTokenFrom(claimUrl),
                    Pin: null,
                    RecipientContactType.Email,
                    _options.RecipientEmail,
                    _options.Password,
                    IdempotencyKey("claim")),
                cancellationToken)
            .ConfigureAwait(false);

        return result.OwnerUserId;
    }

    private async Task<Till> RegisterTillAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        AsSeedActor(scope);

        var registration = scope.ServiceProvider.GetRequiredService<IPosRegistrationService>();

        var client = await registration
            .RegisterClientAsync(
                new RegisterPosClientRequest(_options.TillClientCode, "Demonstration till"),
                cancellationToken)
            .ConfigureAwait(false);

        var terminal = await registration
            .RegisterTerminalAsync(
                client.Id,
                new RegisterPosTerminalRequest(_options.TillTerminalCode, _options.TillStoreReference),
                cancellationToken)
            .ConfigureAwait(false);

        return new Till(client.Id, terminal.Id);
    }

    /// <summary>
    /// Runs the counter journey: the cardholder presents a credential, the till
    /// holds value against it, confirms the sale, and then refunds part of it.
    ///
    /// Each step runs under the principal that really performs it. The credential
    /// is issued by the cardholder, and the hold, confirmation, and refund are
    /// done by the till, which cannot act for any other client.
    /// </summary>
    private async Task TakePaymentAndRefundAsync(
        Guid recipientUserId,
        Guid giftCardId,
        Till till,
        CancellationToken cancellationToken)
    {
        string rawToken;

        await using (var cardholderScope = scopeFactory.CreateAsyncScope())
        {
            Context(cardholderScope).SetIdentityUser(recipientUserId);

            var credential = await cardholderScope.ServiceProvider
                .GetRequiredService<IPaymentTokenService>()
                .IssueAsync(giftCardId, cancellationToken)
                .ConfigureAwait(false);

            rawToken = credential.RawToken;
        }

        Guid provisionId;

        await using (var tillScope = scopeFactory.CreateAsyncScope())
        {
            Context(tillScope).SetPosClient(till.ClientId, till.TerminalId);

            var provision = await tillScope.ServiceProvider
                .GetRequiredService<IPaymentProvisionService>()
                .CreateAsync(
                    new CreatePaymentProvisionRequest(
                        rawToken,
                        PaymentCode: null,
                        _options.PaymentAmount,
                        "DEMO-SALE-001"),
                    cancellationToken)
                .ConfigureAwait(false);

            provisionId = provision.Id;
        }

        await using (var confirmScope = scopeFactory.CreateAsyncScope())
        {
            Context(confirmScope).SetPosClient(till.ClientId, till.TerminalId);

            await confirmScope.ServiceProvider
                .GetRequiredService<IPaymentProvisionService>()
                .ConfirmAsync(
                    provisionId,
                    new ConfirmPaymentProvisionRequest(_options.PaymentAmount),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var refundScope = scopeFactory.CreateAsyncScope();
        Context(refundScope).SetPosClient(till.ClientId, till.TerminalId);

        await refundScope.ServiceProvider
            .GetRequiredService<IPaymentProvisionService>()
            .RefundAsync(
                provisionId,
                new CreatePaymentRefundRequest(
                    _options.RefundAmount,
                    IdempotencyKey("refund"),
                    "DEMO-REFUND-001",
                    "Demonstration partial refund"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls the claim token out of the configured activation link, which may
    /// carry it as a query value or as the final path segment.
    /// </summary>
    private static string ClaimTokenFrom(string claimUrl)
    {
        var uri = new Uri(claimUrl, UriKind.Absolute);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator];
            if (name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("claimToken", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return Uri.UnescapeDataString(uri.Segments[^1].Trim('/'));
    }

    /// <summary>
    /// Stable idempotency keys, so a seed that failed partway reaches the same
    /// financial outcome on a re-run instead of posting a second time.
    /// </summary>
    private string IdempotencyKey(string step) => $"demo-seed:{_options.OrganizationCode}:{step}";

    private static void AsCompanyAdministrator(IServiceScope scope, OrganizationActor actor) =>
        Context(scope).SetOrganizationMember(
            actor.UserId,
            actor.MembershipId,
            actor.OrganizationId,
            actor.OrganizationId);

    private readonly record struct Till(Guid ClientId, Guid TerminalId);

    private static void AsSeedActor(IServiceScope scope) =>
        Context(scope).SetSystem(SeedActorId, SeedPlatformPermissions);

    private static MutableExecutionContext Context(IServiceScope scope) =>
        (MutableExecutionContext)scope.ServiceProvider.GetRequiredService<IExecutionContext>();

    private readonly record struct OrganizationActor(
        Guid UserId,
        Guid MembershipId,
        Guid OrganizationId);
}

internal enum DemoSeedOutcome
{
    Skipped,
    Applied,
    AlreadyApplied,
}
