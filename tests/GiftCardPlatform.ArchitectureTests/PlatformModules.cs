using System.Reflection;

namespace GiftCardPlatform.ArchitectureTests;

/// <summary>
/// The single list of business modules under architectural enforcement.
///
/// This list used to be duplicated in <see cref="ModuleBoundaryTests"/> and
/// <see cref="DomainPurityTests"/>, and both copies had fallen two modules
/// behind: Notifications and Payments were silently exempt from every boundary
/// and domain-purity rule. Nothing failed, because a module that is not named
/// is simply never examined.
///
/// <see cref="EnforcementCoverageTests"/> now compares this list against the
/// module assemblies actually present in the build output, so a new module
/// cannot be added without either appearing here or failing the build.
/// </summary>
internal static class PlatformModules
{
    public const string AssemblyPrefix = "GiftCardPlatform.Modules.";
    public const string ContractsSuffix = ".Contracts";

    /// <summary>
    /// Every business module. Reporting is included deliberately even though it
    /// owns no DbContext and no migrations: it is still bound by the assembly
    /// reference rules. See <see cref="EnforcementCoverageTests"/> for the limit
    /// of what those rules can see.
    /// </summary>
    public static readonly string[] Names =
        [
            "Audit",
            "Authorization",
            "CorporateCredits",
            "Distribution",
            "GiftCards",
            "Identity",
            "Ledger",
            "Notifications",
            "Organizations",
            "Partners",
            "Payments",
            "Reporting",
            "Sharing",
        ];

    public static Assembly Implementation(string module) =>
        Assembly.Load(AssemblyPrefix + module);

    public static Assembly Contracts(string module) =>
        Assembly.Load(AssemblyPrefix + module + ContractsSuffix);

    public static IEnumerable<Assembly> Implementations() =>
        Names.Select(Implementation);

    /// <summary>
    /// Module implementation assemblies sitting next to the test assembly.
    /// Contracts assemblies are excluded; they are reached through their owning
    /// module's name.
    /// </summary>
    public static IReadOnlyCollection<string> DiscoverFromBuildOutput()
    {
        var directory = AppContext.BaseDirectory;

        return Directory
            .EnumerateFiles(directory, AssemblyPrefix + "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name![AssemblyPrefix.Length..])
            .Where(name => !name.EndsWith(ContractsSuffix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }
}
