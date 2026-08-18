namespace GiftCardPlatform.BuildingBlocks.Errors;

/// <summary>
/// Base class for expected application faults that map to a specific HTTP status.
/// Messages must never contain credentials, tokens, or cross-tenant data.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? extensions = null) : base(message)
    {
        Code = code;
        Extensions = extensions;
    }

    public string Code { get; }

    /// <summary>
    /// Optional, curated problem-detail values that help a client locate a
    /// failure. Values must never contain credentials, secrets, or private
    /// cross-tenant data.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; }
}

/// <summary>Input failed validation. Maps to 400.</summary>
public sealed class ValidationFailedException : AppException
{
    public ValidationFailedException(string code, string message)
        : base(code, message)
    {
    }

    public ValidationFailedException(
        string code,
        string message,
        IReadOnlyDictionary<string, object?> extensions)
        : base(code, message, extensions)
    {
    }
}

/// <summary>The caller is authenticated but lacks the required permission. Maps to 403.</summary>
public sealed class ForbiddenException(string code, string message) : AppException(code, message);

public sealed class UnauthorizedException(string code, string message) : AppException(code, message);

/// <summary>The requested resource does not exist or is not visible to the caller. Maps to 404.</summary>
public sealed class NotFoundException(string code, string message) : AppException(code, message);

/// <summary>The request conflicts with existing state, such as a duplicate unique value. Maps to 409.</summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string code, string message)
        : base(code, message)
    {
    }

    public ConflictException(
        string code,
        string message,
        IReadOnlyDictionary<string, object?> extensions)
        : base(code, message, extensions)
    {
    }
}
