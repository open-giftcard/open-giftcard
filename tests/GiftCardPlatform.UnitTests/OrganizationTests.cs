using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Organizations.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class OrganizationCodeTests
{
    [Theory]
    [InlineData("example", "EXAMPLE")]
    [InlineData("  example  ", "EXAMPLE")]
    [InlineData("ExAmPlE", "EXAMPLE")]
    [InlineData("acme-retail", "ACME-RETAIL")]
    [InlineData("acme_logistics", "ACME_LOGISTICS")]
    public void Normalize_trims_and_uppercases(string raw, string expected) =>
        Assert.Equal(expected, OrganizationCode.Normalize(raw));

    [Fact]
    public void Normalize_treats_null_as_empty() =>
        Assert.Equal(string.Empty, OrganizationCode.Normalize(null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_code_is_rejected(string? raw)
    {
        var ex = Assert.Throws<ValidationFailedException>(() => OrganizationCode.NormalizeAndValidate(raw));
        Assert.Equal("organization.code.required", ex.Code);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("THIS_CODE_IS_FAR_TOO_LONG_TO_BE_ACCEPTED")]
    public void Codes_outside_the_allowed_length_are_rejected(string raw)
    {
        var ex = Assert.Throws<ValidationFailedException>(() => OrganizationCode.NormalizeAndValidate(raw));
        Assert.Equal("organization.code.invalid_length", ex.Code);
    }

    [Theory]
    [InlineData("-LEADING")]
    [InlineData("_LEADING")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS.DOT")]
    [InlineData("HAS/SLASH")]
    public void Codes_with_an_invalid_format_are_rejected(string raw)
    {
        var ex = Assert.Throws<ValidationFailedException>(() => OrganizationCode.NormalizeAndValidate(raw));
        Assert.Equal("organization.code.invalid_format", ex.Code);
    }
}

public sealed class OrganizationHierarchyTests
{
    [Fact]
    public void Label_is_a_valid_ltree_label_derived_from_the_identifier()
    {
        var id = Guid.Parse("0192f4c0-1234-7abc-8def-0123456789ab");

        var label = OrganizationHierarchy.CreateLabel(id);

        Assert.Equal("org_0192f4c012347abc8def0123456789ab", label);
        // ltree labels allow only letters, digits and underscores.
        Assert.DoesNotContain("-", label, StringComparison.Ordinal);
        Assert.All(label, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '_'));
    }

    [Fact]
    public void Label_generation_is_deterministic()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(OrganizationHierarchy.CreateLabel(id), OrganizationHierarchy.CreateLabel(id));
    }

    [Fact]
    public void Root_path_is_the_organizations_own_label()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(OrganizationHierarchy.CreateLabel(id), OrganizationHierarchy.CreateRootPath(id));
    }
}

public sealed class OrganizationCreationTests
{
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Root_organization_has_no_parent_and_zero_depth()
    {
        var organization = Organization.CreateRoot("Example Customer Company", "example", Actor, Now);

        Assert.Null(organization.ParentOrganizationId);
        Assert.Equal(0, organization.Depth);
    }

    [Fact]
    public void Root_organization_normalizes_its_code_and_trims_its_name()
    {
        var organization = Organization.CreateRoot("  Example Customer Company  ", " example ", Actor, Now);

        Assert.Equal("EXAMPLE", organization.Code);
        Assert.Equal("Example Customer Company", organization.Name);
    }

    [Fact]
    public void Root_organization_is_created_active_with_a_uuid_v7_identity()
    {
        var organization = Organization.CreateRoot("Example", "EXAMPLE", Actor, Now);

        Assert.Equal(OrganizationStatus.Active, organization.Status);
        Assert.NotEqual(Guid.Empty, organization.Id);
        Assert.Equal(7, organization.Id.Version);
    }

    [Fact]
    public void Root_organization_path_matches_its_own_identifier()
    {
        var organization = Organization.CreateRoot("Example", "EXAMPLE", Actor, Now);

        Assert.Equal(OrganizationHierarchy.CreateRootPath(organization.Id), organization.HierarchyPath);
    }

    [Fact]
    public void Creation_timestamp_is_stored_in_utc()
    {
        var localTime = new DateTimeOffset(2026, 7, 23, 13, 30, 0, TimeSpan.FromHours(3));

        var organization = Organization.CreateRoot("Example", "EXAMPLE", Actor, localTime);

        Assert.Equal(TimeSpan.Zero, organization.CreatedAtUtc.Offset);
        Assert.Equal(localTime.UtcDateTime, organization.CreatedAtUtc.UtcDateTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_name_is_rejected(string? name)
    {
        var ex = Assert.Throws<ValidationFailedException>(
            () => Organization.CreateRoot(name, "EXAMPLE", Actor, Now));

        Assert.Equal("organization.name.required", ex.Code);
    }

    [Fact]
    public void Name_longer_than_the_maximum_is_rejected()
    {
        var ex = Assert.Throws<ValidationFailedException>(
            () => Organization.CreateRoot(new string('x', Organization.NameMaxLength + 1), "EXAMPLE", Actor, Now));

        Assert.Equal("organization.name.invalid_length", ex.Code);
    }

    [Fact]
    public void Missing_creating_user_is_rejected()
    {
        var ex = Assert.Throws<ValidationFailedException>(
            () => Organization.CreateRoot("Example", "EXAMPLE", Guid.Empty, Now));

        Assert.Equal("organization.created_by.required", ex.Code);
    }
}
