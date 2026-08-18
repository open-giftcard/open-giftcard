using System.Reflection;
using NetArchTest.Rules;

namespace GiftCardPlatform.ArchitectureTests;

/// <summary>
/// Enforces the dependency rules accepted in ADR-004, ADR-011, and ADR-022.
/// These run in CI so the module boundaries cannot decay silently.
/// </summary>
public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Modules_do_not_reference_another_modules_implementation_assembly()
    {
        var violations = new List<string>();

        foreach (var module in PlatformModules.Names)
        {
            var referenced = PlatformModules.Implementation(module)
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var other in PlatformModules.Names.Where(m => !string.Equals(m, module, StringComparison.Ordinal)))
            {
                var forbidden = $"GiftCardPlatform.Modules.{other}";

                if (referenced.Contains(forbidden))
                {
                    violations.Add($"{module} references the implementation assembly {forbidden}.");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Cross_module_references_are_limited_to_contracts_assemblies()
    {
        var violations = new List<string>();

        foreach (var module in PlatformModules.Names)
        {
            var crossModuleReferences = PlatformModules.Implementation(module)
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(name =>
                    name is not null &&
                    name.StartsWith("GiftCardPlatform.Modules.", StringComparison.Ordinal) &&
                    !name.StartsWith($"GiftCardPlatform.Modules.{module}", StringComparison.Ordinal));

            foreach (var reference in crossModuleReferences)
            {
                if (!reference!.EndsWith(".Contracts", StringComparison.Ordinal))
                {
                    violations.Add($"{module} references {reference}, which is not a Contracts assembly.");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_projects_do_not_depend_on_implementation_projects()
    {
        var violations = new List<string>();

        foreach (var module in PlatformModules.Names)
        {
            var referenced = PlatformModules.Contracts(module)
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(name =>
                    name is not null &&
                    name.StartsWith("GiftCardPlatform.Modules.", StringComparison.Ordinal) &&
                    !name.EndsWith(".Contracts", StringComparison.Ordinal));

            violations.AddRange(referenced.Select(r => $"{module}.Contracts references implementation assembly {r}."));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void No_module_references_another_modules_db_context()
    {
        var violations = new List<string>();

        foreach (var module in PlatformModules.Names)
        {
            var dbContextTypes = PlatformModules.Implementation(module)
                .GetTypes()
                .Where(t => t.Name.EndsWith("DbContext", StringComparison.Ordinal))
                .ToList();

            // A module's DbContext must not be visible outside its own assembly.
            violations.AddRange(dbContextTypes
                .Where(t => t.IsPublic)
                .Select(t => $"{t.FullName} is public; module DbContexts must stay internal."));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Module_db_contexts_are_never_exposed_through_contracts()
    {
        var violations = new List<string>();

        foreach (var module in PlatformModules.Names)
        {
            violations.AddRange(PlatformModules.Contracts(module)
                .GetTypes()
                .Where(t => t.Name.EndsWith("DbContext", StringComparison.Ordinal))
                .Select(t => $"{t.FullName} is declared in a Contracts assembly."));
        }

        Assert.Empty(violations);
    }
}

/// <summary>
/// Domain code must stay free of infrastructure and transport concerns
/// (ARCHITECTURE.md dependency direction).
/// </summary>
public sealed class DomainPurityTests
{
    private static IEnumerable<Assembly> ModuleAssemblies() =>
        PlatformModules.Implementations();

    private static void AssertDomainDoesNotDependOn(params string[] forbiddenNamespaces)
    {
        foreach (var assembly in ModuleAssemblies())
        {
            var domainTypes = Types.InAssembly(assembly)
                .That()
                .ResideInNamespaceContaining(".Domain");

            // Modules without domain types yet trivially satisfy the rule.
            if (!domainTypes.GetTypes().Any())
            {
                continue;
            }

            var result = domainTypes
                .ShouldNot()
                .HaveDependencyOnAny(forbiddenNamespaces)
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{assembly.GetName().Name} domain types depend on {string.Join(", ", forbiddenNamespaces)}: " +
                string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact]
    public void Domain_does_not_depend_on_entity_framework_core() =>
        AssertDomainDoesNotDependOn("Microsoft.EntityFrameworkCore");

    [Fact]
    public void Domain_does_not_depend_on_asp_net_core() =>
        AssertDomainDoesNotDependOn("Microsoft.AspNetCore");

    [Fact]
    public void Domain_does_not_depend_on_redis_or_elasticsearch() =>
        AssertDomainDoesNotDependOn("StackExchange.Redis", "Elastic", "Nest", "Elasticsearch");

    [Fact]
    public void No_module_depends_on_redis_or_elasticsearch_at_all()
    {
        var forbidden = new[] { "StackExchange.Redis", "Elasticsearch.Net", "NEST", "Elastic.Clients.Elasticsearch" };

        foreach (var assembly in ModuleAssemblies())
        {
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

            foreach (var name in forbidden)
            {
                Assert.DoesNotContain(name, referenced, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
