using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Membership CRUD, authorization, atomicity, the unique constraint, and the
/// controlled platform-operator read path.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class MembershipTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Member_with_create_permission_creates_a_membership()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();

        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsCreate);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MembershipResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal(userId, body.UserId);
        Assert.Equal("Active", body.Status);
        Assert.Null(body.DisabledAtUtc);
        Assert.Equal($"/api/v1/organizations/{organizationId}/memberships/{body.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Member_can_add_an_existing_active_staff_account_by_email()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var email = $"portal-team-{Guid.NewGuid():N}@example.com";
        var user = await CreateStaffUserAsync(email);
        var creator = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.MembershipsCreate);

        var response = await creator.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { email });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipResponse>();
        Assert.NotNull(membership);
        Assert.Equal(user.Id, membership!.UserId);
        Assert.Equal(email, membership.Email);

        var viewer = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.MembershipsView);
        var page = await viewer.GetFromJsonAsync<PagedResponse<MembershipResponse>>(
            $"/api/v1/organizations/{organizationId}/memberships");
        Assert.Contains(
            page!.Items,
            item => item.Id == membership.Id && item.Email == email);

        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.MembershipsView);
        var platformPage = await platform.GetFromJsonAsync<PagedResponse<MembershipResponse>>(
            $"/api/v1/organizations/{organizationId}/memberships");
        Assert.Contains(
            platformPage!.Items,
            item => item.Id == membership.Id && item.Email == email);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Membership_creation_requires_exactly_one_user_selector(
        bool includeUserId,
        bool includeEmail)
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var client = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.MembershipsCreate);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new
            {
                userId = includeUserId ? Guid.CreateVersion7() : (Guid?)null,
                email = includeEmail ? "existing@example.com" : null,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_or_disabled_staff_email_has_one_safe_not_found_result()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var email = $"disabled-team-{Guid.NewGuid():N}@example.com";
        var user = await CreateStaffUserAsync(email);
        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.UsersDisable);
        var disabled = await platform.PostAsync(
            $"/api/v1/users/{user.Id}/disable",
            content: null);
        disabled.EnsureSuccessStatusCode();

        var creator = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.MembershipsCreate);
        foreach (var candidate in new[]
                 {
                     email,
                     $"missing-team-{Guid.NewGuid():N}@example.com",
                 })
        {
            var response = await creator.PostAsJsonAsync(
                $"/api/v1/organizations/{organizationId}/memberships",
                new { email = candidate });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var problem = await response.Content.ReadAsStringAsync();
            Assert.Contains(
                "No active staff account matches this email.",
                problem,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Caller_without_create_permission_cannot_enumerate_staff_email()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var email = $"protected-team-{Guid.NewGuid():N}@example.com";
        await CreateStaffUserAsync(email);
        var viewer = OrganizationMember(
            fixture,
            organizationId,
            OrganizationPermissions.MembershipsView);

        foreach (var candidate in new[]
                 {
                     email,
                     $"missing-team-{Guid.NewGuid():N}@example.com",
                 })
        {
            var response = await viewer.PostAsJsonAsync(
                $"/api/v1/organizations/{organizationId}/memberships",
                new { email = candidate });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Created_membership_is_owned_by_the_callers_organization_and_persisted()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var created = await CreateMembershipAsync(fixture, organizationId);

        // Read back under the organization's own RLS context.
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, transaction, organizationId, isPlatformOperator: false);

        await using var command = new NpgsqlCommand(
            "select organization_id, user_id, status from organizations.organization_memberships where id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", created.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(organizationId, reader.GetGuid(0));
        Assert.Equal(created.UserId, reader.GetGuid(1));
        Assert.Equal("Active", reader.GetString(2));
    }

    [Fact]
    public async Task Creating_a_membership_writes_a_matching_audit_record()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var created = await CreateMembershipAsync(fixture, organizationId);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);
        await using var command = session.Command(
            """
            select operation, entity_type, entity_id, outcome, actor_type,
                   organization_scope_id, actor_membership_id, metadata::text
            from audit.audit_records
            where entity_id = @entity_id and operation = @operation
            """);
        command.Parameters.AddWithValue("entity_id", created.Id.ToString());
        command.Parameters.AddWithValue("operation", AuditOperations.MembershipCreated);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "An audit record must exist for the created membership.");

        Assert.Equal(AuditOperations.MembershipCreated, reader.GetString(0));
        Assert.Equal("OrganizationMembership", reader.GetString(1));
        Assert.Equal(created.Id.ToString(), reader.GetString(2));
        Assert.Equal("Success", reader.GetString(3));
        Assert.Equal("OrganizationMember", reader.GetString(4));
        Assert.Equal(organizationId, reader.GetGuid(5));
        Assert.NotEqual(Guid.Empty, reader.GetGuid(6));

        var metadata = reader.GetString(7);
        Assert.Contains(created.UserId.ToString(), metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("password", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Member_can_list_its_own_organizations_memberships()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var created = await CreateMembershipAsync(fixture, organizationId);

        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsView);
        var response = await client.GetAsync($"/api/v1/organizations/{organizationId}/memberships");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var memberships = await response.Content.ReadFromJsonAsync<PagedResponse<MembershipResponse>>();
        Assert.NotNull(memberships);
        Assert.Contains(memberships!.Items, m => m.Id == created.Id);
    }

    [Fact]
    public async Task Unauthenticated_caller_receives_401()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Member_without_the_create_permission_receives_403()
    {
        var organizationId = await CreateOrganizationAsync(fixture);

        // Authenticated in the organization, but holding only the view permission.
        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsView);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The authenticated actor's own membership is the only row.
        Assert.Equal(1, await CountMembershipsAsync(fixture, organizationId));
    }

    [Fact]
    public async Task Member_cannot_create_in_an_organization_other_than_its_own()
    {
        var ownOrganizationId = await CreateOrganizationAsync(fixture);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);

        // Active organization is its own; the route targets a different organization.
        var client = OrganizationMember(fixture, ownOrganizationId, OrganizationPermissions.MembershipsCreate);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{otherOrganizationId}/memberships",
            new { userId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountMembershipsAsync(fixture, otherOrganizationId));
    }

    [Fact]
    public async Task A_user_cannot_have_two_memberships_in_the_same_organization()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();

        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsCreate);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // One administrator membership plus the one target membership.
        Assert.Equal(2, await CountMembershipsAsync(fixture, organizationId));
    }

    [Fact]
    public async Task Member_with_disable_permission_disables_a_membership()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var created = await CreateMembershipAsync(fixture, organizationId);

        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsDisable);
        var response = await client.PostAsync(
            $"/api/v1/organizations/{organizationId}/memberships/{created.Id}/disable",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MembershipResponse>();
        Assert.Equal("Disabled", body!.Status);
        Assert.NotNull(body.DisabledAtUtc);

        // A disable audit record is written.
        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);
        await using var command = session.Command(
            "select count(*) from audit.audit_records where entity_id = @id and operation = @operation");
        command.Parameters.AddWithValue("id", created.Id.ToString());
        command.Parameters.AddWithValue("operation", AuditOperations.MembershipDisabled);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Disabling_an_already_disabled_membership_is_rejected()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var created = await CreateMembershipAsync(fixture, organizationId);

        var client = OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsDisable);
        var disableUrl = $"/api/v1/organizations/{organizationId}/memberships/{created.Id}/disable";

        var first = await client.PostAsync(disableUrl, content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsync(disableUrl, content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Platform_operator_can_read_any_organizations_memberships_through_the_policy_path()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var created = await CreateMembershipAsync(fixture, organizationId);

        // A platform operator is not scoped to this organization, yet may read it
        // through the controlled RLS path (read-only).
        var client = PlatformOperator(fixture, PlatformPermissions.MembershipsView);
        var response = await client.GetAsync($"/api/v1/organizations/{organizationId}/memberships");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var memberships = await response.Content.ReadFromJsonAsync<PagedResponse<MembershipResponse>>();
        Assert.Contains(memberships!.Items, m => m.Id == created.Id);
    }

    [Fact]
    public async Task Platform_operator_without_the_memberships_view_permission_is_denied()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        await CreateMembershipAsync(fixture, organizationId);

        var client = PlatformOperator(fixture, PlatformPermissions.OrganizationsView);
        var response = await client.GetAsync($"/api/v1/organizations/{organizationId}/memberships");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Development_OpenAPI_exposes_only_the_additive_team_contract()
    {
        var response = await fixture.Factory.CreateClient()
            .GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var membershipInput = schemas
            .GetProperty("CreateMembershipApiRequest")
            .GetProperty("properties");
        Assert.True(membershipInput.TryGetProperty("userId", out _));
        Assert.True(membershipInput.TryGetProperty("email", out _));

        var membershipOutput = schemas
            .GetProperty("MembershipApiResponse")
            .GetProperty("properties");
        Assert.True(membershipOutput.TryGetProperty("email", out _));
        Assert.False(membershipOutput.TryGetProperty("password", out _));
        Assert.False(membershipOutput.TryGetProperty("phoneNumber", out _));
        Assert.False(membershipOutput.TryGetProperty("normalizedEmail", out _));

        var assignments = document.RootElement
            .GetProperty("paths")
            .GetProperty(
                "/api/v1/organizations/{organizationId}/roles/assignments");
        Assert.True(assignments.TryGetProperty("get", out _));
        Assert.True(assignments.TryGetProperty("post", out _));
    }

    [Fact]
    public async Task Membership_and_its_audit_record_commit_atomically()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();
        var actorUserId = Guid.CreateVersion7();
        await ProvisionOrganizationActorAsync(
            fixture,
            actorUserId,
            organizationId,
            [OrganizationPermissions.MembershipsCreate]);
        var membershipCountBefore = await CountMembershipsAsync(fixture, organizationId);

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
        client.DefaultRequestHeaders.Add(OrganizationIdHeader, organizationId.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Neither the membership nor an audit row may survive the rolled-back unit.
        Assert.Equal(membershipCountBefore, await CountMembershipsAsync(fixture, organizationId));

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);
        await using var auditCommand = session.Command(
            "select count(*) from audit.audit_records where metadata->>'user_id' = @user_id");
        auditCommand.Parameters.AddWithValue("user_id", userId.ToString());
        Assert.Equal(0L, (long)(await auditCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// Wraps the real recorder so the audit row genuinely joins the transaction
    /// and is then abandoned by a failure. Registered only by this test.
    /// </summary>
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

    private async Task<StaffUserResponse> CreateStaffUserAsync(string email)
    {
        var platform = PlatformOperator(
            fixture,
            PlatformPermissions.UsersCreate);
        var response = await platform.PostAsJsonAsync(
            "/api/v1/users",
            new
            {
                email,
                password = "Portal team test passphrase 2026!",
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StaffUserResponse>())!;
    }

    private sealed record StaffUserResponse(Guid Id, string Email);
}
