namespace GiftCardPlatform.Api;

/// <summary>
/// Source-generated request logging (REVIEW-001, M3).
///
/// The fields are named rather than interpolated so a log collector can index
/// and filter on them. It carries the acting user and organization but never
/// request bodies, headers, or credentials.
/// </summary>
internal static partial class RequestLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "{Method} {Path} responded {StatusCode} in {ElapsedMilliseconds}ms " +
                  "UserId={UserId} OrganizationId={OrganizationId}")]
    public static partial void Completed(
        ILogger logger,
        string method,
        string path,
        int statusCode,
        double elapsedMilliseconds,
        Guid? userId,
        Guid? organizationId);
}
