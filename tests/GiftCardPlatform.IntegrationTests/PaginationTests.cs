using System.Net;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// List endpoints are bounded (REVIEW-001, M1). An unbounded list becomes a
/// denial-of-service vector once a customer has tens of thousands of members.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class PaginationTests(PlatformApiFixture fixture)
{
    private async Task<Guid> OrganizationWithMembershipsAsync(int count)
    {
        var organizationId = await CreateOrganizationAsync(fixture);

        for (var i = 0; i < count; i++)
        {
            await CreateMembershipAsync(fixture, organizationId);
        }

        return organizationId;
    }

    private HttpClient Reader(Guid organizationId) =>
        OrganizationMember(fixture, organizationId, OrganizationPermissions.MembershipsView);

    [Fact]
    public async Task A_page_is_limited_and_reports_that_more_remain()
    {
        var organizationId = await OrganizationWithMembershipsAsync(5);

        var response = await Reader(organizationId)
            .GetAsync($"/api/v1/organizations/{organizationId}/memberships?limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<MembershipResponse>>();
        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(2, page.Limit);
        Assert.Equal(0, page.Offset);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task The_last_page_reports_that_nothing_remains()
    {
        var organizationId = await OrganizationWithMembershipsAsync(3);

        var response = await Reader(organizationId)
            .GetAsync($"/api/v1/organizations/{organizationId}/memberships?limit=10");

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<MembershipResponse>>();
        Assert.Equal(4, page!.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Offsetting_walks_the_list_without_repeating_or_skipping()
    {
        var organizationId = await OrganizationWithMembershipsAsync(5);
        var seen = new List<Guid>();

        for (var offset = 0; offset < 6; offset += 2)
        {
            var response = await Reader(organizationId)
                .GetAsync($"/api/v1/organizations/{organizationId}/memberships?limit=2&offset={offset}");

            var page = await response.Content.ReadFromJsonAsync<PagedResponse<MembershipResponse>>();
            seen.AddRange(page!.Items.Select(m => m.Id));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_default_page_size_applies_when_none_is_requested()
    {
        var organizationId = await OrganizationWithMembershipsAsync(2);

        var response = await Reader(organizationId)
            .GetAsync($"/api/v1/organizations/{organizationId}/memberships");

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<MembershipResponse>>();
        Assert.Equal(PageRequest.DefaultLimit, page!.Limit);
        Assert.Equal(3, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageRequest.MaxLimit + 1)]
    public async Task An_out_of_range_limit_is_rejected(int limit)
    {
        var organizationId = await CreateOrganizationAsync(fixture);

        // Rejected rather than clamped, so a caller that believes it is fetching
        // everything finds out that it is not.
        var response = await Reader(organizationId)
            .GetAsync($"/api/v1/organizations/{organizationId}/memberships?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_negative_offset_is_rejected()
    {
        var organizationId = await CreateOrganizationAsync(fixture);

        var response = await Reader(organizationId)
            .GetAsync($"/api/v1/organizations/{organizationId}/memberships?offset=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subsidiary_listing_is_paged_too()
    {
        var parentId = await CreateOrganizationAsync(fixture);
        var creator = OrganizationMember(fixture, parentId, OrganizationPermissions.CreateSubsidiary);

        for (var i = 0; i < 3; i++)
        {
            var created = await creator.PostAsJsonAsync(
                $"/api/v1/organizations/{parentId}/subsidiaries",
                new { name = "Paged Subsidiary", code = "PG" + Guid.NewGuid().ToString("N")[..13].ToUpperInvariant() });
            created.EnsureSuccessStatusCode();
        }

        var client = OrganizationMember(fixture, parentId, OrganizationPermissions.View);
        var response = await client.GetAsync($"/api/v1/organizations/{parentId}/subsidiaries?limit=2");

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<SubsidiaryPageItem>>();
        Assert.Equal(2, page!.Items.Count);
        Assert.True(page.HasMore);
    }

    private sealed record SubsidiaryPageItem(Guid Id, string Name, string Code);
}

/// <summary>
/// Optimistic concurrency on mutable rows (REVIEW-001, M5). The token is
/// PostgreSQL's <c>xmin</c>, so a read-modify-write that races another writer
/// fails instead of silently overwriting it.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class ConcurrencyTokenTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task A_write_against_a_stale_version_affects_no_rows()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);

        // Read the current version, as EF does before an update.
        uint version;
        await using (var read = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId))
        {
            await using var command = read.Command(
                "select xmin from organizations.organization_memberships where id = @id");
            command.Parameters.AddWithValue("id", membership.Id);
            version = (uint)(await command.ExecuteScalarAsync())!;
        }

        // Someone else changes the row, which advances xmin.
        await using (var other = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId))
        {
            await using var command = other.Command(
                "update organizations.organization_memberships set status = 'Disabled' where id = @id");
            command.Parameters.AddWithValue("id", membership.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            await other.CommitAsync();
        }

        // The original writer's update, guarded by the version it read, matches
        // nothing — which is what EF turns into a concurrency exception.
        await using var stale = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);
        await using var update = stale.Command(
            """
            update organizations.organization_memberships
            set status = 'Active'
            where id = @id and xmin = @version
            """);
        update.Parameters.AddWithValue("id", membership.Id);
        update.Parameters.AddWithValue("version", NpgsqlTypes.NpgsqlDbType.Xid, version);

        Assert.Equal(0, await update.ExecuteNonQueryAsync());
    }
}
