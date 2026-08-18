using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Organizations.Domain;

namespace GiftCardPlatform.UnitTests;

/// <summary>
/// Hierarchy path and depth computation for subsidiaries (ADR-010). Label and
/// root-path helpers are covered by <c>OrganizationHierarchyTests</c>.
/// </summary>
public sealed class OrganizationSubsidiaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static Organization CreateRoot() =>
        Organization.CreateRoot("Root Company", "ROOT" + Guid.NewGuid().ToString("N")[..8], Guid.CreateVersion7(), Now);

    private static Organization CreateChild(Organization parent, int maxDepth = OrganizationHierarchy.DefaultMaxDepth) =>
        Organization.CreateSubsidiary(
            parent,
            "Child Company",
            "CHILD" + Guid.NewGuid().ToString("N")[..8],
            Guid.CreateVersion7(),
            Now,
            maxDepth);

    [Fact]
    public void Subsidiary_sits_one_level_below_its_parent()
    {
        var root = CreateRoot();

        var child = CreateChild(root);

        Assert.Equal(root.Id, child.ParentOrganizationId);
        Assert.Equal(root.Depth + 1, child.Depth);
        Assert.Equal(1, child.Depth);
    }

    [Fact]
    public void Subsidiary_path_extends_the_parent_path_with_its_own_label()
    {
        var root = CreateRoot();

        var child = CreateChild(root);

        Assert.Equal($"{root.HierarchyPath}.org_{child.Id:N}", child.HierarchyPath);
        Assert.StartsWith(root.HierarchyPath + ".", child.HierarchyPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_subsidiaries_accumulate_path_segments()
    {
        var root = CreateRoot();
        var first = CreateChild(root);
        var second = CreateChild(first);

        Assert.Equal(2, second.Depth);
        Assert.Equal($"org_{root.Id:N}.org_{first.Id:N}.org_{second.Id:N}", second.HierarchyPath);
    }

    [Fact]
    public void A_subsidiary_is_active_and_carries_the_creating_user()
    {
        var root = CreateRoot();
        var createdBy = Guid.CreateVersion7();

        var child = Organization.CreateSubsidiary(
            root, "Child Company", "CHILDCODE", createdBy, Now, OrganizationHierarchy.DefaultMaxDepth);

        Assert.Equal(OrganizationStatus.Active, child.Status);
        Assert.Equal(createdBy, child.CreatedByUserId);
        Assert.Equal(Now, child.CreatedAtUtc);
    }

    [Fact]
    public void The_deepest_allowed_level_is_accepted()
    {
        // Depth is zero-based, so five levels means depths 0 through 4.
        var current = CreateRoot();

        for (var level = 1; level < OrganizationHierarchy.DefaultMaxDepth; level++)
        {
            current = CreateChild(current);
            Assert.Equal(level, current.Depth);
        }

        Assert.Equal(OrganizationHierarchy.DefaultMaxDepth - 1, current.Depth);
    }

    [Fact]
    public void Exceeding_the_maximum_depth_is_rejected()
    {
        var current = CreateRoot();

        for (var level = 1; level < OrganizationHierarchy.DefaultMaxDepth; level++)
        {
            current = CreateChild(current);
        }

        var deepest = current;
        var ex = Assert.Throws<ValidationFailedException>(() => CreateChild(deepest));
        Assert.Equal("organization.hierarchy.max_depth_exceeded", ex.Code);
    }

    [Fact]
    public void The_depth_limit_is_configurable()
    {
        var root = CreateRoot();

        // A limit of one level allows the root only.
        var ex = Assert.Throws<ValidationFailedException>(() => CreateChild(root, maxDepth: 1));
        Assert.Equal("organization.hierarchy.max_depth_exceeded", ex.Code);

        // A limit of two levels admits one subsidiary, but not a grandchild.
        var child = CreateChild(root, maxDepth: 2);
        Assert.Equal(1, child.Depth);

        var grandchild = Assert.Throws<ValidationFailedException>(() => CreateChild(child, maxDepth: 2));
        Assert.Equal("organization.hierarchy.max_depth_exceeded", grandchild.Code);
    }

    [Fact]
    public void A_subsidiary_requires_a_creating_user()
    {
        var root = CreateRoot();

        var ex = Assert.Throws<ValidationFailedException>(() => Organization.CreateSubsidiary(
            root, "Child Company", "CHILDCODE", Guid.Empty, Now, OrganizationHierarchy.DefaultMaxDepth));

        Assert.Equal("organization.created_by.required", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    public void A_subsidiary_validates_its_name(string? name)
    {
        var root = CreateRoot();

        Assert.Throws<ValidationFailedException>(() => Organization.CreateSubsidiary(
            root, name, "CHILDCODE", Guid.CreateVersion7(), Now, OrganizationHierarchy.DefaultMaxDepth));
    }

    [Fact]
    public void A_subsidiary_validates_its_code()
    {
        var root = CreateRoot();

        var ex = Assert.Throws<ValidationFailedException>(() => Organization.CreateSubsidiary(
            root, "Child Company", "has space", Guid.CreateVersion7(), Now, OrganizationHierarchy.DefaultMaxDepth));

        Assert.Equal("organization.code.invalid_format", ex.Code);
    }
}
