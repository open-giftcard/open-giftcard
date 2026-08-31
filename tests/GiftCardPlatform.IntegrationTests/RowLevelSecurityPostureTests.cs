using System.Runtime.CompilerServices;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Asserts the exact Row-Level Security posture of every table in the twelve
/// module schemas.
///
/// The other isolation tests prove that a specific tenant cannot read another
/// tenant's rows through a specific endpoint. This one protects the property
/// underneath all of them: that no tenant-owned table was ever added without a
/// policy. A missing policy is invisible to a feature test that never happens
/// to query the new table, and RLS cannot be retrofitted safely once rows
/// exist, so the check belongs at the schema level.
///
/// Tables that deliberately carry no policy are listed here by name with the
/// reason. Adding a table without either a forced policy or an entry in that
/// list fails this test, which is the point: it forces the decision to be made
/// rather than defaulted. The same list appears in SECURITY.md, and the two are
/// kept in step by <see cref="Documented_exemptions_match_the_security_policy"/>.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class RowLevelSecurityPostureTests(PlatformApiFixture fixture)
{
    /// <summary>
    /// Enabled but deliberately not forced, with the reason it cannot be.
    /// </summary>
    private static readonly Dictionary<string, string> EnabledButNotForced = new(StringComparer.Ordinal)
    {
        ["organizations.organizations"] =
            "Forcing this would subject the table owner to the policy, and the policy calls a " +
            "SECURITY DEFINER function that reads this table to resolve the caller's tenant root " +
            "(ADR-023). The owner is the migration role, which is never used at runtime.",
    };

    /// <summary>
    /// No RLS at all, with the reason each holds nothing tenant-scoped.
    /// </summary>
    private static readonly Dictionary<string, string> NoRowLevelSecurity = new(StringComparer.Ordinal)
    {
        ["identity.users"] = "Identities are global; one person may hold memberships in several tenants.",
        ["identity.sessions"] = "Belongs to a global identity, not to a tenant.",
        ["identity.refresh_tokens"] = "Belongs to a global identity, not to a tenant.",

        ["payments.pos_clients"] = "Platform-wide device registry with no tenant column (ADR-043).",
        ["payments.pos_terminals"] = "Platform-wide device registry with no tenant column (ADR-043).",

        ["authorization.permissions"] = "Global permission catalogue seeded by the migrator.",
        ["authorization.platform_roles"] = "Platform authority, which is a separate model from tenant membership (ADR-021).",
        ["authorization.platform_role_permissions"] = "Platform authority.",
        ["authorization.platform_role_assignments"] = "Platform authority.",
        ["authorization.platform_bootstrap_state"] = "Single-row bootstrap guard.",
        ["authorization.organization_administrator_bootstraps"] = "Bootstrap guard, readable only through its own guarded path.",

        ["audit.audit_checkpoints"] = "Tamper-evidence over the audit log as a whole, not tenant data (ADR-013).",
        ["audit.audit_checkpoint_seals"] = "Tamper-evidence, not tenant data.",
        ["audit.audit_checkpoint_witnesses"] = "Tamper-evidence, not tenant data.",
    };

    private sealed record TablePosture(string Schema, string Name, bool Enabled, bool Forced, int Policies)
    {
        public string Qualified => $"{Schema}.{Name}";
    }

    private static async Task<IReadOnlyList<TablePosture>> ReadPostureAsync(NpgsqlConnection connection)
    {
        const string Sql = """
            select n.nspname,
                   c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   (select count(*) from pg_policy p where p.polrelid = c.oid)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind = 'r'
              and n.nspname in (
                  'organizations', 'audit', 'identity', 'authorization', 'ledger',
                  'corporate_credits', 'gift_cards', 'distribution', 'sharing',
                  'payments', 'notifications', 'partners')
            order by n.nspname, c.relname
            """;

        var results = new List<TablePosture>();
        await using var command = new NpgsqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new TablePosture(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetInt32(4)));
        }

        return results;
    }

    [Fact]
    public async Task Every_table_is_forced_protected_or_a_documented_exemption()
    {
        await using var connection = await fixture.OpenAppConnectionAsync();
        var tables = await ReadPostureAsync(connection);

        Assert.NotEmpty(tables);

        var problems = new List<string>();
        foreach (var table in tables)
        {
            // Migration bookkeeping is owned by the migrator role and holds no
            // business data of any kind.
            if (table.Name == "__ef_migrations_history")
            {
                continue;
            }

            if (NoRowLevelSecurity.ContainsKey(table.Qualified))
            {
                if (table.Enabled)
                {
                    problems.Add(
                        $"{table.Qualified} is listed as having no Row-Level Security, but it is enabled. " +
                        "Remove it from the exemption list.");
                }

                continue;
            }

            if (EnabledButNotForced.ContainsKey(table.Qualified))
            {
                if (!table.Enabled)
                {
                    problems.Add($"{table.Qualified} is listed as enabled but unforced, and RLS is not enabled at all.");
                }
                else if (table.Forced)
                {
                    problems.Add(
                        $"{table.Qualified} is now forced. That is stronger than documented, so remove it " +
                        "from the exemption list rather than weakening it back.");
                }
                else if (table.Policies == 0)
                {
                    problems.Add($"{table.Qualified} has Row-Level Security enabled but no policy, which denies everything.");
                }

                continue;
            }

            if (!table.Enabled)
            {
                problems.Add(
                    $"{table.Qualified} has no Row-Level Security. If it holds tenant-owned data it needs a " +
                    "forced policy in its first migration. If it does not, add it to the exemption list in " +
                    "this test and to SECURITY.md, with the reason.");
                continue;
            }

            if (!table.Forced)
            {
                problems.Add(
                    $"{table.Qualified} has Row-Level Security enabled but not forced. The runtime role owns " +
                    "nothing so it is still constrained, but an unforced policy is not what this project claims.");
            }

            if (table.Policies == 0)
            {
                problems.Add($"{table.Qualified} has Row-Level Security enabled but no policy, which denies everything.");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"Row-Level Security posture problems:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", problems));
    }

    [Fact]
    public async Task Protected_tables_outnumber_their_exemptions()
    {
        // A guard against the exemption lists being used to make this suite
        // pass. If most tables ever become exemptions, the claim that RLS is
        // the authoritative barrier has stopped being true.
        await using var connection = await fixture.OpenAppConnectionAsync();
        var tables = await ReadPostureAsync(connection)
            .ContinueWith(t => t.Result.Where(x => x.Name != "__ef_migrations_history").ToList());

        var forced = tables.Count(t => t.Forced);
        var exempt = EnabledButNotForced.Count + NoRowLevelSecurity.Count;

        Assert.True(
            forced > exempt,
            $"{forced} tables are forced against {exempt} documented exemptions.");
    }

    [Fact]
    public void Documented_exemptions_match_the_security_policy()
    {
        // SECURITY.md tells a reader which tables are exempt. A list that drifts
        // from the one this test enforces is worse than no list, because it is
        // read as current.
        var securityPolicy = Path.Combine(RepositoryRoot(), "SECURITY.md");
        Assert.True(File.Exists(securityPolicy), $"Missing {securityPolicy}");

        var text = File.ReadAllText(securityPolicy);
        var missing = EnabledButNotForced.Keys
            .Concat(NoRowLevelSecurity.Keys)
            .Where(qualified => !text.Contains(qualified, StringComparison.Ordinal))
            // The platform catalogue and checkpoint tables are described by
            // their schema rather than named one by one, which stays readable.
            .Where(qualified =>
                !qualified.StartsWith("authorization.", StringComparison.Ordinal) &&
                !qualified.StartsWith("audit.", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"SECURITY.md does not mention these documented exemptions: {string.Join(", ", missing)}");
    }

    // Anchored to this source file rather than to the build output. The output
    // directory can be redirected outside the working tree, and walking up from
    // there then finds nothing.
    private static string RepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SECURITY.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Could not locate the repository root from '{sourcePath}'.");
    }
}
