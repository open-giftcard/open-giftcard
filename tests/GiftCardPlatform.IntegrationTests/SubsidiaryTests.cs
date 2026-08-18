using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Subsidiary creation and the organization hierarchy (ADR-010): parent scope
/// taken from the trusted execution context, ltree path and depth computed
/// server-side, and the configured depth limit enforced before insert.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class SubsidiaryTests(PlatformApiFixture fixture)
{
    // Random (v4): a UUID v7's leading hex is a timestamp and collides per millisecond.
    private static string UniqueCode() => "SUB" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private sealed record SubsidiaryResponse(
        Guid Id,
        Guid ParentOrganizationId,
        string Name,
        string Code,
        string Status,
        int Depth,
        DateTimeOffset CreatedAtUtc);

    private HttpClient Creator(Guid organizationId) =>
        OrganizationMember(fixture, organizationId, OrganizationPermissions.CreateSubsidiary);

    /// <summary>Creates a subsidiary of <paramref name="parentId"/>, acting as that organization.</summary>
    private async Task<SubsidiaryResponse> CreateSubsidiaryAsync(Guid parentId, string? code = null)
    {
        var response = await Creator(parentId).PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Subsidiary Company", code = code ?? UniqueCode() });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<SubsidiaryResponse>())!;
    }

    [Fact]
    public async Task Member_with_the_permission_creates_a_subsidiary()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var code = UniqueCode();

        var response = await Creator(parentId).PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Example Customer Retail", code });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubsidiaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(parentId, body!.ParentOrganizationId);
        Assert.Equal("Example Customer Retail", body.Name);
        Assert.Equal(code, body.Code);
        Assert.Equal("Active", body.Status);
        Assert.Equal(1, body.Depth);
    }

    [Fact]
    public async Task Stored_subsidiary_has_the_correct_parent_depth_and_ltree_path()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(parentId);

        // Read as the owning tenant: the organizations table is behind RLS.
        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, parentId);
        await using var command = session.Command(
            """
            select parent_organization_id, depth, hierarchy_path::text
            from organizations.organizations
            where id = @id
            """);
        command.Parameters.AddWithValue("id", child.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(parentId, reader.GetGuid(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal($"org_{parentId:N}.org_{child.Id:N}", reader.GetString(2));
    }

    [Fact]
    public async Task Member_cannot_create_a_subsidiary_under_another_organization()
    {
        var ownOrganizationId = await CreateOrganizationAsync(fixture);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var code = UniqueCode();

        // Active organization is its own; the route targets a different tenant.
        var response = await Creator(ownOrganizationId).PostAsJsonAsync(
            $"/api/v1/organizations/{otherOrganizationId}/subsidiaries",
            new { name = "Hijacked Subsidiary", code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOrganizationsWithCodeAsync(code));
    }

    [Fact]
    public async Task Member_without_the_permission_is_denied()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var code = UniqueCode();

        // Authenticated in the organization, but holding an unrelated permission.
        var client = OrganizationMember(fixture, parentId, OrganizationPermissions.MembershipsView);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Denied Subsidiary", code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOrganizationsWithCodeAsync(code));
    }

    [Fact]
    public async Task Unauthenticated_caller_receives_401()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Anonymous Subsidiary", code = UniqueCode() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_platform_operator_cannot_create_a_customer_subsidiary()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var code = UniqueCode();

        // Subsidiary creation is an organization-scoped operation in this slice.
        var client = PlatformOperator(fixture, PlatformPermissions.OrganizationsCreate);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Platform Subsidiary", code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOrganizationsWithCodeAsync(code));
    }

    [Fact]
    public async Task The_configured_maximum_depth_is_enforced()
    {
        // Depth is zero-based: a five-level limit permits depths 0 through 4.
        var currentId = await CreateOrganizationAsync(fixture);

        for (var level = 1; level < OrganizationHierarchy.DefaultMaxDepth; level++)
        {
            var created = await CreateSubsidiaryAsync(currentId);
            Assert.Equal(level, created.Depth);
            currentId = created.Id;
        }

        // One level deeper must be rejected before insert.
        var code = UniqueCode();
        var response = await Creator(currentId).PostAsJsonAsync(
            $"/api/v1/organizations/{currentId}/subsidiaries",
            new { name = "Too Deep", code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountOrganizationsWithCodeAsync(code));
    }

    [Fact]
    public async Task A_subsidiary_cannot_be_created_under_a_non_active_parent()
    {
        var parentId = await CreateOrganizationAsync(fixture);

        // Suspending a root organization is a platform action; the RLS policy
        // admits a platform write only when the row has no parent.
        await using (var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture))
        {
            await using var suspend = session.Command(
                "update organizations.organizations set status = 'Suspended' where id = @id");
            suspend.Parameters.AddWithValue("id", parentId);
            Assert.Equal(1, await suspend.ExecuteNonQueryAsync());

            await session.CommitAsync();
        }

        var code = UniqueCode();
        var response = await Creator(parentId).PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Suspended Parent Subsidiary", code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountOrganizationsWithCodeAsync(code));
    }

    [Fact]
    public async Task A_caller_cannot_authenticate_in_a_non_existent_parent()
    {
        var missingId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, missingId.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{missingId}/subsidiaries",
            new { name = "Orphan Subsidiary", code = UniqueCode() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_organization_codes_are_rejected()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var code = UniqueCode();

        await CreateSubsidiaryAsync(parentId, code);

        var second = await Creator(parentId).PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Duplicate Subsidiary", code = code.ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountOrganizationsWithCodeAsync(code));
    }

    /// <summary>
    /// ADR-024: subsidiary codes are unique per tenant, not globally. Two
    /// customers must be able to use the same code, and neither may learn of the
    /// other's codes by provoking a conflict.
    /// </summary>
    [Fact]
    public async Task Two_tenants_can_use_the_same_subsidiary_code()
    {
        var tenantA = await CreateOrganizationAsync(fixture);
        var tenantB = await CreateOrganizationAsync(fixture);
        var sharedCode = UniqueCode();

        var first = await CreateSubsidiaryAsync(tenantA, sharedCode);

        // The same code under a different customer must be accepted, not a 409.
        var response = await Creator(tenantB).PostAsJsonAsync(
            $"/api/v1/organizations/{tenantB}/subsidiaries",
            new { name = "Other Tenant Retail", code = sharedCode });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var second = await response.Content.ReadFromJsonAsync<SubsidiaryResponse>();
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Equal(sharedCode, second.Code);

        // Both rows exist, each owned by its own tenant.
        Assert.Equal(2, await CountOrganizationsWithCodeAsync(sharedCode));
    }

    [Fact]
    public async Task A_subsidiary_code_may_reuse_another_tenants_root_code_namespace_safely()
    {
        // A root code is globally unique, so a second root with the same code is
        // still rejected — the platform namespace is unchanged by ADR-024.
        var platform = PlatformOperator(fixture, PlatformPermissions.OrganizationsCreate);
        var code = UniqueCode();

        var first = await platform.PostAsJsonAsync("/api/v1/organizations", new { name = "Root One", code });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await platform.PostAsJsonAsync("/api/v1/organizations", new { name = "Root Two", code });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Creating_a_subsidiary_writes_a_matching_audit_record()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var child = await CreateSubsidiaryAsync(parentId);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, parentId);
        await using var command = session.Command(
            """
            select operation, entity_type, entity_id, outcome, actor_type,
                   organization_scope_id, actor_membership_id, metadata::text
            from audit.audit_records
            where entity_id = @entity_id and operation = @operation
            """);
        command.Parameters.AddWithValue("entity_id", child.Id.ToString());
        command.Parameters.AddWithValue("operation", AuditOperations.SubsidiaryCreated);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "An audit record must exist for the created subsidiary.");

        Assert.Equal(AuditOperations.SubsidiaryCreated, reader.GetString(0));
        Assert.Equal("Organization", reader.GetString(1));
        Assert.Equal(child.Id.ToString(), reader.GetString(2));
        Assert.Equal("Success", reader.GetString(3));
        Assert.Equal("OrganizationMember", reader.GetString(4));
        // The acting tenant scope is the parent organization.
        Assert.Equal(parentId, reader.GetGuid(5));
        Assert.NotEqual(Guid.Empty, reader.GetGuid(6));

        var metadata = reader.GetString(7);
        Assert.Contains(parentId.ToString(), metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("password", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_direct_subsidiaries()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var unrelatedId = await CreateOrganizationAsync(fixture);

        var first = await CreateSubsidiaryAsync(parentId);
        var second = await CreateSubsidiaryAsync(parentId);
        var grandchild = await CreateSubsidiaryAsync(first.Id);
        var unrelatedChild = await CreateSubsidiaryAsync(unrelatedId);

        var client = OrganizationMember(fixture, parentId, OrganizationPermissions.View);
        var response = await client.GetAsync($"/api/v1/organizations/{parentId}/subsidiaries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var subsidiaries = await response.Content.ReadFromJsonAsync<PagedResponse<SubsidiaryResponse>>();
        var ids = subsidiaries!.Items.Select(s => s.Id).ToList();

        Assert.Contains(first.Id, ids);
        Assert.Contains(second.Id, ids);
        // Direct children only, and never another tenant's.
        Assert.DoesNotContain(grandchild.Id, ids);
        Assert.DoesNotContain(unrelatedChild.Id, ids);
    }

    [Fact]
    public async Task Listing_requires_the_view_permission_and_the_callers_own_organization()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var otherId = await CreateOrganizationAsync(fixture);
        await CreateSubsidiaryAsync(parentId);

        var withoutPermission = OrganizationMember(fixture, parentId, OrganizationPermissions.MembershipsView);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await withoutPermission.GetAsync($"/api/v1/organizations/{parentId}/subsidiaries")).StatusCode);

        var wrongOrganization = OrganizationMember(fixture, otherId, OrganizationPermissions.View);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await wrongOrganization.GetAsync($"/api/v1/organizations/{parentId}/subsidiaries")).StatusCode);
    }

    [Fact]
    public async Task Subsidiary_and_its_audit_record_commit_atomically()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var code = UniqueCode();
        var actorUserId = Guid.CreateVersion7();
        await ProvisionOrganizationActorAsync(
            fixture,
            actorUserId,
            parentId,
            [OrganizationPermissions.CreateSubsidiary]);

        using var factory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.ConfigureServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAuditRecorder));
                services.Remove(original);

                services.Add(ServiceDescriptor.Describe(
                    typeof(IAuditRecorder),
                    sp =>
                    {
                        var inner = (IAuditRecorder)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                        return new FailAfterWritingAuditRecorder(inner);
                    },
                    original.Lifetime));
            }));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(actorUserId));
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, parentId.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{parentId}/subsidiaries",
            new { name = "Rolled Back Subsidiary", code });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Neither the organization row nor an audit row may survive.
        Assert.Equal(0, await CountOrganizationsWithCodeAsync(code));

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, parentId);
        await using var auditCommand = session.Command(
            "select count(*) from audit.audit_records where metadata->>'code' = @code");
        auditCommand.Parameters.AddWithValue("code", code);
        Assert.Equal(0L, (long)(await auditCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// Counts across all tenants, so it runs as a platform operator — the
    /// organizations table is behind RLS and a context-free connection sees
    /// nothing.
    /// </summary>
    private async Task<int> CountOrganizationsWithCodeAsync(string code)
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);

        return (int)await session.ScalarCountAsync(
            "select count(*) from organizations.organizations where code = @code",
            command => command.Parameters.AddWithValue("code", code.ToUpperInvariant()));
    }

    /// <summary>Registered only by this test, to abandon a written audit row.</summary>
    private sealed class FailAfterWritingAuditRecorder(IAuditRecorder inner) : IAuditRecorder
    {
        public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            await inner.RecordAsync(entry, cancellationToken);
            throw new InvalidOperationException("Simulated audit failure after the audit row was written.");
        }

        public Task RecordIndependentlyAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            inner.RecordIndependentlyAsync(entry, cancellationToken);
    }
}
