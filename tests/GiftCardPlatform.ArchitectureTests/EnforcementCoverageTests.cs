using System.Reflection;

namespace GiftCardPlatform.ArchitectureTests;

/// <summary>
/// Guards the guards.
///
/// Every other test in this project iterates <see cref="PlatformModules.Names"/>.
/// A module missing from that list is not reported as a violation; it is simply
/// never looked at, so the suite stays green while the boundary it was supposed
/// to protect goes unchecked. That is exactly what happened to Notifications and
/// Payments, which sat outside enforcement while the project described these
/// tests as the guarantee that the design holds.
/// </summary>
public sealed class EnforcementCoverageTests
{
    [Fact]
    public void Every_module_assembly_in_the_build_output_is_under_enforcement()
    {
        var discovered = PlatformModules.DiscoverFromBuildOutput();
        var enforced = PlatformModules.Names.ToHashSet(StringComparer.Ordinal);

        var unenforced = discovered.Except(enforced).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            unenforced.Count == 0,
            "These module assemblies exist but are absent from PlatformModules.Names, so no " +
            $"architecture rule examines them: {string.Join(", ", unenforced)}. Add them to the " +
            "list rather than deleting this test.");
    }

    [Fact]
    public void Every_enforced_module_actually_exists()
    {
        var discovered = PlatformModules.DiscoverFromBuildOutput();

        var missing = PlatformModules.Names
            .Where(name => !discovered.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "PlatformModules.Names lists modules with no assembly in the build output, so the " +
            $"rules covering them silently do nothing: {string.Join(", ", missing)}.");
    }

    [Fact]
    public void Every_module_has_a_contracts_assembly()
    {
        var violations = new List<string>();

        foreach (var module in PlatformModules.Names)
        {
            try
            {
                PlatformModules.Contracts(module);
            }
            catch (FileNotFoundException)
            {
                violations.Add(
                    $"{module} has no {PlatformModules.ContractsSuffix} assembly, so other modules " +
                    "have no legal way to depend on it.");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Records a real blind spot rather than implying coverage that does not exist.
    ///
    /// The boundary rules work on assembly references. Reporting owns no DbContext
    /// and no migrations; it composes read-only queries by issuing raw SQL against
    /// other modules' schemas through the shared scoped connection. That is a
    /// deliberate cross-module read, accepted for reporting, and it is invisible to
    /// every reference-based rule here.
    ///
    /// This test pins the shape that makes the exception safe: Reporting stays
    /// read-only by owning no persistence of its own. If it ever gains a DbContext,
    /// the exception no longer holds and the decision needs revisiting.
    /// </summary>
    [Fact]
    public void Reporting_owns_no_persistence_so_its_raw_sql_reads_stay_read_only()
    {
        var reporting = PlatformModules.Implementation("Reporting");

        var dbContexts = reporting
            .GetTypes()
            .Where(t => t.Name.EndsWith("DbContext", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(
            dbContexts.Count == 0,
            "Reporting has gained a DbContext: " + string.Join(", ", dbContexts) + ". It was " +
            "allowed to read other modules' schemas with raw SQL precisely because it owns no " +
            "persistence and writes nothing. Revisit that decision before adding one.");
    }
}
