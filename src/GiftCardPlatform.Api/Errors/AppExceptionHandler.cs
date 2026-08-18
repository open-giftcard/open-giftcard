using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Audit.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Errors;

/// <summary>
/// Maps expected application faults to ProblemDetails responses. Unexpected
/// exceptions fall through to a generic 500 so internal details, stack traces,
/// and cross-tenant data are never exposed.
///
/// Registered as a singleton by <c>AddExceptionHandler</c>, so the per-request
/// <see cref="IExecutionContext"/> is resolved from the request scope inside
/// <see cref="TryHandleAsync"/> rather than injected.
/// </summary>
internal sealed partial class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "Unhandled exception. CorrelationId={CorrelationId}")]
    private static partial void LogUnhandled(ILogger logger, Guid correlationId, Exception exception);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Authorization denied. CorrelationId={CorrelationId} Path={Path} Code={Code}")]
    private static partial void LogDenied(ILogger logger, Guid correlationId, string path, string code);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Failed to record a denial audit entry. CorrelationId={CorrelationId}")]
    private static partial void LogDenialAuditFailed(ILogger logger, Guid correlationId, Exception exception);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var executionContext = httpContext.RequestServices.GetRequiredService<IExecutionContext>();
        var correlationId = executionContext.CorrelationId;

        if (exception is ForbiddenException denial)
        {
            await RecordDenialAsync(httpContext, executionContext, denial, cancellationToken);
        }

        var (status, title) = exception switch
        {
            ValidationFailedException => (StatusCodes.Status400BadRequest, "Invalid request."),
            UnauthorizedException => (StatusCodes.Status401Unauthorized, "Unauthorized."),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden."),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found."),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict."),

            // A malformed or unparseable body is the caller's mistake, not ours.
            // Without this it surfaced as a 500, which both misleads the client
            // and buries real faults in the error logs.
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request."),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, correlationId, exception);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            // Only curated messages from AppException reach the client.
            Detail = exception is AppException appException ? appException.Message : null,
            Instance = httpContext.Request.Path,
        };

        problem.Extensions["correlationId"] = correlationId;

        if (exception is AppException coded)
        {
            problem.Extensions["code"] = coded.Code;
            if (coded.Extensions is not null)
            {
                foreach (var (key, value) in coded.Extensions)
                {
                    if (key is "code" or "correlationId")
                    {
                        continue;
                    }

                    problem.Extensions[key] = value;
                }
            }
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    /// <summary>
    /// Records a refused operation so tenant-boundary probing is visible
    /// (ADR-025).
    ///
    /// This is handled centrally rather than in each application service: a
    /// single chokepoint cannot be forgotten when a new service is added, and
    /// every <see cref="ForbiddenException"/> passes through here regardless of
    /// which module raised it. Entry points that do not run through this
    /// pipeline — background jobs, when they exist — must record their own.
    ///
    /// Only authenticated callers are recorded. An unauthenticated request has no
    /// principal worth attributing and would let anyone fill the audit table by
    /// hammering a protected route.
    ///
    /// The record is written on its own connection so it survives the rollback of
    /// the operation that was refused, and a failure to write it never changes
    /// the response the caller receives.
    /// </summary>
    private async Task RecordDenialAsync(
        HttpContext httpContext,
        IExecutionContext executionContext,
        ForbiddenException denial,
        CancellationToken cancellationToken)
    {
        LogDenied(logger, executionContext.CorrelationId, httpContext.Request.Path, denial.Code);

        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            return;
        }

        try
        {
            var recorder = httpContext.RequestServices.GetRequiredService<IAuditRecorder>();

            await recorder.RecordIndependentlyAsync(
                new AuditEntry(
                    ActorUserId: executionContext.UserId.Value,
                    ActorType: executionContext.IsPlatformOperator
                        ? AuditActorType.PlatformOperator
                        : executionContext.ActiveMembershipId is not null
                            ? AuditActorType.OrganizationMember
                            : AuditActorType.IdentityUser,
                    ActorMembershipId: executionContext.ActiveMembershipId,
                    OrganizationScopeId: executionContext.ActiveOrganizationId,
                    Operation: AuditOperations.AuthorizationDenied,
                    EntityType: "HttpEndpoint",
                    EntityId: httpContext.Request.Path.ToString(),
                    Outcome: AuditOutcome.Failure,
                    CorrelationId: executionContext.CorrelationId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["method"] = httpContext.Request.Method,
                        ["reason"] = denial.Code,
                    }),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Auditing a denial must never turn a clean 403 into a 500.
            LogDenialAuditFailed(logger, executionContext.CorrelationId, ex);
        }
    }
}
