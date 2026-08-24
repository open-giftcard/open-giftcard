using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using GiftCardPlatform.Api;
using GiftCardPlatform.Api.Authentication;
using GiftCardPlatform.Api.Demo;
using GiftCardPlatform.Api.Endpoints;
using GiftCardPlatform.Api.Errors;
using GiftCardPlatform.Api.Services;
using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization;
using GiftCardPlatform.Modules.CorporateCredits;
using GiftCardPlatform.Modules.Distribution;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.GiftCards;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Identity;
using GiftCardPlatform.Modules.Ledger;
using GiftCardPlatform.Modules.Notifications;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Organizations;
using GiftCardPlatform.Modules.Partners;
using GiftCardPlatform.Modules.Payments;
using GiftCardPlatform.Modules.Reporting;
using GiftCardPlatform.Modules.Sharing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using Npgsql;

// Migration mode applies every module's schema as the migration owner and exits.
// It is a separate process on purpose: the API runs as a role that owns nothing
// and cannot alter its own schema.
if (DatabaseMigrator.IsRequested(args))
{
    return await DatabaseMigrator.RunAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);
var knownProxyAddresses = builder.Configuration
    .GetSection("Networking:ForwardedHeaders:KnownProxies")
    .GetChildren()
    .Select(item => item.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Select(value =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException(
                $"Networking:ForwardedHeaders:KnownProxies contains invalid IP address '{value}'."))
    .Distinct()
    .ToArray();

// Register only an explicit console provider. The Windows default provider set
// includes Event Log, which an unprivileged development/test process may not
// write; logging an otherwise handled exception must never replace the response
// with an EventLog access failure.
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole();
}
else
{
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

var connectionString =
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. See README.md for local development setup.");

builder.Services.AddBuildingBlocks(connectionString);
builder.Services.AddOrganizationsModule(builder.Configuration);
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddAuthorizationModule(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddLedgerModule();
builder.Services.AddCorporateCreditsModule();
builder.Services.AddGiftCardsModule();
builder.Services.AddDistributionModule(builder.Configuration);
builder.Services.AddReportingModule();
builder.Services.AddSharingModule(builder.Configuration);
builder.Services.AddPaymentsModule(builder.Configuration);
builder.Services.AddPartnersModule(builder.Configuration);
builder.Services.AddNotificationsModule(builder.Configuration);
ObservabilityConfiguration.Configure(
    builder.Services,
    builder.Configuration,
    builder.Environment);

// Readiness compares the applied migrations against this build. Singleton
// because a confirmed-current schema is cached for the process lifetime.
builder.Services.AddSingleton<SchemaReadiness>();
builder.Services.AddHostedService<ShareExpirationWorker>();
builder.Services.AddHostedService<PaymentProvisionExpirationWorker>();

// Outbox payload protection. Development gets a repository-local key ring;
// every other environment must name durable storage shared by all instances.
// Startup fails before serving traffic when that deployment contract is absent.
DataProtectionConfiguration.Configure(
    builder.Services,
    builder.Configuration,
    builder.Environment);
builder.Services.AddSingleton<INotificationPayloadProtector>(serviceProvider =>
    new DataProtectionNotificationProtector(
        serviceProvider.GetRequiredService<IDataProtectionProvider>()));

var smtpOptions = builder.Configuration
    .GetSection(SmtpNotificationOptions.SectionName)
    .Get<SmtpNotificationOptions>() ?? new SmtpNotificationOptions();
if (smtpOptions.Enabled)
{
    if (string.IsNullOrWhiteSpace(smtpOptions.Host) ||
        string.IsNullOrWhiteSpace(smtpOptions.FromAddress) ||
        string.IsNullOrWhiteSpace(smtpOptions.Password))
    {
        // Fail at startup rather than dead-lettering every activation link.
        throw new InvalidOperationException(
            "Notifications:Smtp requires Host, FromAddress, and Password when enabled. " +
            "Supply the password through user secrets or the environment; never commit it.");
    }

    builder.Services.AddSingleton<INotificationChannelSender>(
        new SmtpNotificationSender(smtpOptions));
}
else if (builder.Environment.IsDevelopment())
{
    // No mail server: capture instead, so the whole activation journey is still
    // demonstrable through the Development console.
    builder.Services.AddSingleton<INotificationChannelSender>(
        new CapturingNotificationSender(NotificationChannel.Email));
}

// SMS has no adapter yet. Outside Development, channel availability rejects a
// phone operation before its business transaction commits.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<INotificationChannelSender>(
        new CapturingNotificationSender(NotificationChannel.Sms));
}

// Development-only demonstration seed. Registered only here, so outside
// Development the hosted service does not exist and no configuration can turn it
// on. It is additionally off unless Demo:Seed:Enabled is set.
if (builder.Environment.IsDevelopment())
{
    builder.Services.Configure<DemoSeedOptions>(
        builder.Configuration.GetSection(DemoSeedOptions.SectionName));
    builder.Services.AddSingleton<DemoSeeder>();
    builder.Services.AddHostedService<DemoSeedHostedService>();
}

builder.Services.AddHostedService<NotificationDispatcherWorker>();
var auditCheckpointOptions = builder.Configuration
    .GetSection(AuditCheckpointOptions.SectionName)
    .Get<AuditCheckpointOptions>() ?? new AuditCheckpointOptions();
if (auditCheckpointOptions.Enabled)
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Audit checkpointing outside Development requires an external KMS/HSM signer " +
            "and immutable witness adapter; no production provider is selected.");
    }

    if (string.IsNullOrWhiteSpace(auditCheckpointOptions.DevelopmentSigningKeyPath) ||
        string.IsNullOrWhiteSpace(auditCheckpointOptions.DevelopmentWitnessDirectory))
    {
        throw new InvalidOperationException(
            "Development audit checkpointing requires explicit signing-key and witness paths.");
    }

    builder.Services.AddSingleton<IAuditCheckpointSigner>(serviceProvider =>
        new DevelopmentFileAuditCheckpointSigner(
            auditCheckpointOptions.DevelopmentSigningKeyPath));
    builder.Services.AddSingleton<IAuditCheckpointWitness>(serviceProvider =>
        new DevelopmentFileAuditCheckpointWitness(
            auditCheckpointOptions.DevelopmentWitnessDirectory,
            serviceProvider.GetRequiredService<TimeProvider>()));
}
builder.Services.AddHostedService<AuditCheckpointWorker>();
builder.Services.AddOptions<GiftCardExpirationOptions>()
    .BindConfiguration(GiftCardExpirationOptions.SectionName)
    .Validate(
        options => options.PollIntervalSeconds is >= 5 and <= 86_400,
        "GiftCards:Expiration:PollIntervalSeconds must be between 5 and 86400.")
    .Validate(
        options => options.BatchSize is >= 1 and <= 100,
        "GiftCards:Expiration:BatchSize must be between 1 and 100.")
    .ValidateOnStart();
builder.Services.AddHostedService<GiftCardExpirationWorker>();
builder.Services.AddOptions<BulkGiftCardBatchOptions>()
    .BindConfiguration(BulkGiftCardBatchOptions.SectionName)
    .Validate(
        options => options.PollIntervalSeconds is >= 1 and <= 86_400,
        "Distribution:BulkBatches:PollIntervalSeconds must be between 1 and 86400.")
    .Validate(
        options => options.ChunkSize is >= 1 and <= 100,
        "Distribution:BulkBatches:ChunkSize must be between 1 and 100.")
    .ValidateOnStart();
builder.Services.AddHostedService<BulkGiftCardBatchWorker>();

builder.Services.AddPlatformAuthentication(builder.Configuration);
var loginPermitLimit =
    builder.Configuration.GetValue<int?>("Authentication:LoginRateLimit:PermitLimit") ?? 5;
var bootstrapPermitLimit =
    builder.Configuration.GetValue<int?>("Bootstrap:RateLimit:PermitLimit") ?? 5;
var claimPermitLimit =
    builder.Configuration.GetValue<int?>("Distribution:ClaimRateLimit:PermitLimit") ?? 10;
var paymentPermitLimit =
    builder.Configuration.GetValue<int?>("Payments:RedemptionRateLimit:PermitLimit") ?? 60;
// Its own budget, and partitioned per terminal rather than per client, so a lane
// reading balances cannot exhaust the allowance its shop needs to take payments,
// and a single misbehaving lane is contained. A cashier inquires about once per
// sale, so this is generous for real use and still bounds how fast a stolen
// device token could sweep balances.
var balanceInquiryPermitLimit =
    builder.Configuration.GetValue<int?>("Payments:BalanceInquiryRateLimit:PermitLimit") ?? 30;
// Deliberately tighter than the payment limit. A reseller exchanges credentials
// once per token lifetime, not once per order, so a legitimate integration needs
// very few; anything more is a brute-force attempt against a minting credential.
var partnerAuthPermitLimit =
    builder.Configuration.GetValue<int?>("Partners:AuthRateLimit:PermitLimit") ?? 10;
if (loginPermitLimit < 1)
{
    throw new InvalidOperationException(
        "Authentication:LoginRateLimit:PermitLimit must be greater than zero.");
}
if (bootstrapPermitLimit < 1)
{
    throw new InvalidOperationException(
        "Bootstrap:RateLimit:PermitLimit must be greater than zero.");
}
if (claimPermitLimit < 1)
{
    throw new InvalidOperationException(
        "Distribution:ClaimRateLimit:PermitLimit must be greater than zero.");
}
if (partnerAuthPermitLimit < 1)
{
    throw new InvalidOperationException(
        "Partners:AuthRateLimit:PermitLimit must be greater than zero.");
}
if (paymentPermitLimit < 1)
{
    throw new InvalidOperationException(
        "Payments:RedemptionRateLimit:PermitLimit must be greater than zero.");
}
if (balanceInquiryPermitLimit < 1)
{
    throw new InvalidOperationException(
        "Payments:BalanceInquiryRateLimit:PermitLimit must be greater than zero.");
}

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(IdentityEndpoints.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy(BootstrapEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = bootstrapPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy(DistributionEndpoints.ClaimRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = claimPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy(PartnerEndpoints.AuthRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = partnerAuthPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy(PaymentEndpoints.RateLimitPolicy, context =>
    {
        var executionContext = context.RequestServices
            .GetRequiredService<IExecutionContext>();
        var partition = executionContext.PosClientId is { } posClientId
            ? $"pos:{posClientId:N}"
            : executionContext.UserId is { } userId
                ? $"user:{userId:N}"
                : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = paymentPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
    options.AddPolicy(PaymentEndpoints.BalanceInquiryRateLimitPolicy, context =>
    {
        var executionContext = context.RequestServices
            .GetRequiredService<IExecutionContext>();
        var partition = executionContext.PosTerminalId is { } posTerminalId
            ? $"pos-terminal:{posTerminalId:N}"
            : executionContext.PosClientId is { } posClientId
                ? $"pos:{posClientId:N}"
                : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = balanceInquiryPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
if (knownProxyAddresses.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var address in knownProxyAddresses)
        {
            options.KnownProxies.Add(address);
        }
    });
}

// Enums cross the wire as their names, not their ordinals. A POS or mobile
// client sending "Subtree" is readable and stable; an ordinal silently changes
// meaning if the enum is ever reordered.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Digital Corporate Gift Card Platform",
        Version = "v1",
        Description = "Multi-tenant gift-card platform with identity, authorization, audit, and ledger-backed corporate credit.",
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT access token returned by POST /api/v1/auth/login or /refresh.",
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
    });

});

var app = builder.Build();
if (knownProxyAddresses.Length > 0)
{
    app.UseForwardedHeaders();
}

// Seed the correlation id before authentication populates the identity, so even
// anonymous and failed requests are traceable.
//
// The value is always generated server-side and never taken from the request. It
// ends up in audit records, so a caller must not be able to choose it: that would
// let them tie their actions to an identifier of their choosing, or reuse another
// operation's id to muddy an investigation (REVIEW-001, M4). A client-supplied
// X-Correlation-Id is echoed back as X-Request-Id for trace stitching only, and
// carries no authority.
app.Use(async (context, next) =>
{
    var executionContext = context.RequestServices.GetRequiredService<MutableExecutionContext>();

    var correlationId = Guid.CreateVersion7();
    executionContext.SetCorrelationId(correlationId);
    context.Response.Headers["X-Correlation-Id"] = correlationId.ToString();

    if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var clientSupplied) &&
        !string.IsNullOrWhiteSpace(clientSupplied))
    {
        context.Response.Headers["X-Request-Id"] = clientSupplied.ToString();
    }

    // Every log line written during this request carries the correlation id, so
    // an audit record can be tied back to the logs that explain it (REVIEW-001,
    // M3). Health probes are excluded to keep the signal readable.
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("GiftCardPlatform.Api.Request");
    var metrics = context.RequestServices.GetRequiredService<PlatformMetrics>();

    using var scope = logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
    });

    var isProbe = context.Request.Path.StartsWithSegments("/health");
    var started = Stopwatch.GetTimestamp();

    await next();

    if (!isProbe)
    {
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? "unmatched";
        metrics.RecordHttpRequest(
            context.Request.Method,
            route,
            context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started));

        // CA1873: the analyzer cannot see that the argument expressions are
        // already behind the IsEnabled guard below, so they are only evaluated
        // when the log will actually be written.
#pragma warning disable CA1873
        if (logger.IsEnabled(LogLevel.Information))
        {
            RequestLog.Completed(
                logger,
                context.Request.Method,
                context.Request.Path.ToString(),
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                // Recorded after authentication, so denied requests still show who asked.
                executionContext.UserId,
                executionContext.ActiveOrganizationId);
        }
#pragma warning restore CA1873
    }
});

// Keep the exception handler inside the correlation, request-log, and metrics
// middleware so handled errors are recorded with their final response status.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "GiftCardPlatform v1");
        options.RoutePrefix = "swagger";
    });

    // Development-only demonstration UI. Not mapped outside Development.
    app.MapDemoEndpoints();
}

app.MapOrganizationEndpoints();
app.MapCurrentUserEndpoints();
app.MapMembershipEndpoints();
app.MapSubsidiaryEndpoints();
app.MapRoleEndpoints();
app.MapIdentityEndpoints();
app.MapBootstrapEndpoints();
app.MapCorporateCreditEndpoints();
app.MapGiftCardEndpoints();
app.MapDistributionEndpoints(app.Environment);
app.MapReportingEndpoints();
app.MapSharingEndpoints(app.Environment);
app.MapPaymentEndpoints();
app.MapPosEndpoints();
app.MapPartnerEndpoints();

// Liveness: the process is up. Deliberately does not touch the database, so a
// database outage does not cause orchestrators to kill otherwise healthy pods.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .ExcludeFromDescription();

// Readiness: this instance can actually serve a request. A load balancer that
// only checks liveness will route traffic to instances that cannot reach
// PostgreSQL (REVIEW-001, M2).
app.MapGet("/health/ready", async (
    ScopedDatabaseConnection database,
    SchemaReadiness schema,
    PlatformMetrics metrics,
    CancellationToken cancellationToken) =>
{
    try
    {
        var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);

        // Connectivity is not readiness. A database that answers can still be
        // carrying a schema older than this build, in which case the first
        // request touching a new column fails with 42703 while every probe
        // reports healthy.
        var behind = await schema.GetModulesBehindAsync(cancellationToken);
        if (behind.Count > 0)
        {
            metrics.SetReadiness(false);
            return Results.Json(
                new { status = "migrations-pending", modules = behind },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        metrics.SetReadiness(true);
        return Results.Ok(new { status = "ready" });
    }
    catch (NpgsqlException)
    {
        metrics.SetReadiness(false);
        // Deliberately does not echo the connection error: it can contain host
        // and credential detail, and this endpoint is unauthenticated.
        return Results.Json(
            new { status = "unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
    .AllowAnonymous()
    .ExcludeFromDescription();

await app.RunAsync();
return 0;

/// <summary>Exposed so the integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
