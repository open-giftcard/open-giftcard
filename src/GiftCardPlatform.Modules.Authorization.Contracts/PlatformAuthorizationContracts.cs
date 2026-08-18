namespace GiftCardPlatform.Modules.Authorization.Contracts;

public sealed class PlatformBootstrapOptions
{
    public const string SectionName = "Bootstrap:PlatformAdministrator";

    public string Secret { get; set; } = string.Empty;
}

public sealed record BootstrapPlatformAdministratorRequest(
    string? Secret,
    string? Email,
    string? Password);

public sealed record PlatformAdministratorBootstrapResult(
    Guid UserId,
    string Email,
    Guid PlatformRoleId,
    DateTimeOffset CompletedAtUtc);

public sealed record InitialOrganizationAdministratorResult(
    Guid OrganizationId,
    Guid UserId,
    Guid MembershipId,
    Guid RoleId,
    Guid RoleAssignmentId,
    DateTimeOffset AssignedAtUtc);

public interface IPlatformPermissionResolver
{
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken);
}

public interface IPlatformBootstrapService
{
    Task<PlatformAdministratorBootstrapResult> BootstrapAsync(
        BootstrapPlatformAdministratorRequest request,
        CancellationToken cancellationToken);
}

public interface IInitialOrganizationAdministratorService
{
    Task<InitialOrganizationAdministratorResult> AssignAsync(
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken);
}
