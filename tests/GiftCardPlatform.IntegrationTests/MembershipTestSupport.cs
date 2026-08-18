using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Shared helpers for the organization-membership tests: client builders for the
/// two caller kinds (platform operator vs organization-scoped member) and small
/// raw-SQL utilities used to prove RLS independently of the application query.
/// </summary>
internal static class MembershipTestSupport
{
    private static readonly ConcurrentDictionary<Guid, Guid> DefaultActors = new();

    public const string OrganizationIdHeader = "X-Organization-Id";
    public const string UntrustedPermissionsHeader = "X-Untrusted-Organization-Permissions";

    public static HttpClient PlatformOperator(PlatformApiFixture fixture, params string[] permissions)
        => PlatformOperator(fixture, Guid.CreateVersion7(), permissions);

    public static HttpClient PlatformOperator(
        PlatformApiFixture fixture,
        Guid userId,
        params string[] permissions)
    {
        var bootstrap = new ActorBootstrapHandler(
            () => ProvisionPlatformActorAsync(fixture, userId, permissions));
        var client = fixture.Factory.CreateDefaultClient(bootstrap);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static HttpClient OrganizationMember(
        PlatformApiFixture fixture,
        Guid organizationId,
        params string[] permissions) =>
        OrganizationMember(
            fixture,
            DefaultActors.GetOrAdd(organizationId, _ => Guid.CreateVersion7()),
            organizationId,
            permissions);

    public static HttpClient OrganizationMember(
        PlatformApiFixture fixture,
        Guid userId,
        Guid organizationId,
        params string[] permissions)
    {
        var bootstrap = new ActorBootstrapHandler(
            () => ProvisionOrganizationActorAsync(fixture, userId, organizationId, permissions));
        var client = fixture.Factory.CreateDefaultClient(bootstrap);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static async Task ProvisionPlatformActorAsync(
        PlatformApiFixture fixture,
        Guid userId,
        IReadOnlyCollection<string> permissions)
    {
        var grantable = permissions
            .Where(PlatformPermissions.IsKnown)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (grantable.Length == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var roleId = Guid.CreateVersion7();
        await using (var role = new NpgsqlCommand(
            """
            insert into "authorization".platform_roles
                (id, name, is_system, created_at_utc)
            values (@id, @name, false, now())
            """,
            connection,
            transaction))
        {
            role.Parameters.AddWithValue("id", roleId);
            role.Parameters.AddWithValue("name", "TEST-" + Guid.NewGuid().ToString("N"));
            await role.ExecuteNonQueryAsync();
        }

        foreach (var permissionName in grantable)
        {
            await using var grant = new NpgsqlCommand(
                """
                insert into "authorization".platform_role_permissions
                    (id, role_id, permission)
                values (@id, @role_id, @permission)
                """,
                connection,
                transaction);
            grant.Parameters.AddWithValue("id", Guid.CreateVersion7());
            grant.Parameters.AddWithValue("role_id", roleId);
            grant.Parameters.AddWithValue("permission", permissionName);
            await grant.ExecuteNonQueryAsync();
        }

        await using (var assignment = new NpgsqlCommand(
            """
            insert into "authorization".platform_role_assignments
                (id, user_id, role_id, created_at_utc)
            values (@id, @user_id, @role_id, now())
            """,
            connection,
            transaction))
        {
            assignment.Parameters.AddWithValue("id", Guid.CreateVersion7());
            assignment.Parameters.AddWithValue("user_id", userId);
            assignment.Parameters.AddWithValue("role_id", roleId);
            await assignment.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Provisions authentication/authorization prerequisites directly through
    /// the test database so each focused test can choose an exact permission
    /// set. Requests still authenticate with signed JWTs and authorize through
    /// the production application path.
    /// </summary>
    public static async Task<Guid> ProvisionOrganizationActorAsync(
        PlatformApiFixture fixture,
        Guid userId,
        Guid organizationId,
        IReadOnlyCollection<string> permissions)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, transaction, organizationId, isPlatformOperator: false);

        var membershipId = await FindMembershipIdAsync(connection, transaction, userId, organizationId);
        if (membershipId is null)
        {
            membershipId = Guid.CreateVersion7();
            await using var membership = new NpgsqlCommand(
                """
                insert into organizations.organization_memberships
                    (id, organization_id, user_id, status, created_at_utc, disabled_at_utc)
                values (@id, @organization_id, @user_id, 'Active', now(), null)
                """,
                connection,
                transaction);
            membership.Parameters.AddWithValue("id", membershipId.Value);
            membership.Parameters.AddWithValue("organization_id", organizationId);
            membership.Parameters.AddWithValue("user_id", userId);
            await membership.ExecuteNonQueryAsync();
        }

        var grantable = permissions
            .Where(OrganizationPermissions.IsKnown)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (grantable.Length > 0)
        {
            var roleId = Guid.CreateVersion7();
            await using var role = new NpgsqlCommand(
                """
                insert into "authorization".roles
                    (id, organization_id, name, created_at_utc, created_by_user_id)
                values (@id, @organization_id, @name, now(), @user_id)
                """,
                connection,
                transaction);
            role.Parameters.AddWithValue("id", roleId);
            role.Parameters.AddWithValue("organization_id", organizationId);
            role.Parameters.AddWithValue("name", "TEST-" + Guid.NewGuid().ToString("N"));
            role.Parameters.AddWithValue("user_id", userId);
            await role.ExecuteNonQueryAsync();

            foreach (var permissionName in grantable)
            {
                await using var grant = new NpgsqlCommand(
                    """
                    insert into "authorization".role_permissions
                        (id, role_id, organization_id, permission)
                    values (@id, @role_id, @organization_id, @permission)
                    """,
                    connection,
                    transaction);
                grant.Parameters.AddWithValue("id", Guid.CreateVersion7());
                grant.Parameters.AddWithValue("role_id", roleId);
                grant.Parameters.AddWithValue("organization_id", organizationId);
                grant.Parameters.AddWithValue("permission", permissionName);
                await grant.ExecuteNonQueryAsync();
            }

            await using var assignment = new NpgsqlCommand(
                """
                insert into "authorization".membership_role_assignments
                    (id, organization_id, membership_id, role_id, scope_type,
                     anchor_organization_id, created_at_utc, created_by_user_id)
                values (@id, @organization_id, @membership_id, @role_id, 'Organization',
                        @organization_id, now(), @user_id)
                """,
                connection,
                transaction);
            assignment.Parameters.AddWithValue("id", Guid.CreateVersion7());
            assignment.Parameters.AddWithValue("organization_id", organizationId);
            assignment.Parameters.AddWithValue("membership_id", membershipId.Value);
            assignment.Parameters.AddWithValue("role_id", roleId);
            assignment.Parameters.AddWithValue("user_id", userId);
            await assignment.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return membershipId.Value;
    }

    private static async Task<Guid?> FindMembershipIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid organizationId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id
            from organizations.organization_memberships
            where organization_id = @organization_id and user_id = @user_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("user_id", userId);
        return (Guid?)await command.ExecuteScalarAsync();
    }

    private sealed class ActorBootstrapHandler(Func<Task> bootstrap) : DelegatingHandler
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool provisioned;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!provisioned)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (!provisioned)
                    {
                        await bootstrap();
                        provisioned = true;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Creates a customer organization as a platform operator and returns its id.</summary>
    public static async Task<Guid> CreateOrganizationAsync(PlatformApiFixture fixture)
    {
        var client = PlatformOperator(fixture, PlatformPermissions.OrganizationsCreate);
        var code = "MEMORG" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Membership Test Organization", code });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OrgIdResponse>();
        return body!.Id;
    }

    /// <summary>Creates a membership as an organization-scoped member and returns it.</summary>
    public static async Task<MembershipResponse> CreateMembershipAsync(
        PlatformApiFixture fixture,
        Guid organizationId,
        Guid? userId = null)
    {
        var client = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.MembershipsCreate);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId = userId ?? Guid.CreateVersion7() });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<MembershipResponse>())!;
    }

    /// <summary>
    /// Establishes the RLS session context on a raw connection, exactly as the
    /// application's transaction coordinator does, so raw-SQL tests exercise the
    /// same policy path.
    /// </summary>
    public static async Task SetSessionContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? organizationId,
        bool isPlatformOperator,
        Guid? userId = null)
    {
        await using var command = new NpgsqlCommand(
            """
            select
                set_config('app.user_id', @user, true),
                set_config('app.organization_id', @org, true),
                set_config('app.is_platform_operator', @platform, true)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user", userId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("platform", isPlatformOperator ? "true" : "false");

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Counts memberships visible for an organization under its own RLS context.</summary>
    public static async Task<long> CountMembershipsAsync(PlatformApiFixture fixture, Guid organizationId)
    {
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, transaction, organizationId, isPlatformOperator: false);

        await using var command = new NpgsqlCommand(
            "select count(*) from organizations.organization_memberships where organization_id = @org",
            connection,
            transaction);
        command.Parameters.AddWithValue("org", organizationId);

        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return count;
    }

    internal sealed record OrgIdResponse(Guid Id);

    /// <summary>Mirrors the API's paged list envelope.</summary>
    internal sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Limit, int Offset, bool HasMore);

    internal sealed record MembershipResponse(
        Guid Id,
        Guid OrganizationId,
        Guid UserId,
        string? Email,
        string Status,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? DisabledAtUtc);
}
