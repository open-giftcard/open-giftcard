using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <summary>
    /// Puts the organizations table behind Row-Level Security (ADR-023).
    ///
    /// The predicate is the **tenant boundary**: a caller may see the customer
    /// hierarchy it belongs to, identified by root_organization_id. Finer-grained
    /// "which part of my own tenant may I act on" is authorization's job
    /// (ADR-006 scope evaluation) and is enforced above this layer, so this policy
    /// is written once and does not need revisiting when Subtree scope arrives.
    ///
    /// The caller's tenant root is resolved by a SECURITY DEFINER function rather
    /// than carried in a session variable, because resolving it in the application
    /// would require reading this very table before the session context exists.
    /// </summary>
    public partial class AddOrganizationsRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Resolves the tenant root of the organization named by the current
            // session context. SECURITY DEFINER so it runs as the table owner and
            // is therefore not filtered by the policy below — without that, the
            // policy would depend on a query the policy itself blocks.
            //
            // STABLE so PostgreSQL evaluates it once per query rather than per row.
            // search_path is pinned so the function cannot be redirected to a
            // shadowing object.
            //
            // It takes no arguments and reveals nothing beyond the caller's own
            // session context, so the default PUBLIC execute grant is safe and
            // keeps this migration independent of the runtime role's name, which
            // differs between environments.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION organizations.caller_root_organization_id()
                RETURNS uuid
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, organizations
                AS $fn$
                    SELECT o.root_organization_id
                    FROM organizations.organizations o
                    WHERE o.id = nullif(current_setting('app.organization_id', true), '')::uuid
                $fn$;
                """);

            // ENABLE, deliberately not FORCE. FORCE would subject the table owner
            // to the policy too, which would break the SECURITY DEFINER lookup
            // above. The owner is the migration role, which by ADR-019 is never
            // used at runtime; the runtime application role owns nothing and so
            // remains fully subject to the policy.
            migrationBuilder.Sql(
                "ALTER TABLE organizations.organizations ENABLE ROW LEVEL SECURITY;");

            // USING governs visibility: a platform operator reads across tenants,
            // and a customer caller sees its own hierarchy.
            //
            // WITH CHECK governs written rows. A platform operator may only write
            // rows with no parent — that is, create root customer organizations —
            // so it cannot inject a subsidiary into a customer's tree. Every other
            // write must land inside the caller's own tenant.
            migrationBuilder.Sql(
                """
                CREATE POLICY organizations_tenant_isolation
                    ON organizations.organizations
                    USING (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        OR root_organization_id = organizations.caller_root_organization_id()
                    )
                    WITH CHECK (
                        (
                            coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                            AND parent_organization_id IS NULL
                        )
                        OR root_organization_id = organizations.caller_root_organization_id()
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS organizations_tenant_isolation ON organizations.organizations;");

            migrationBuilder.Sql(
                "ALTER TABLE organizations.organizations DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS organizations.caller_root_organization_id();");
        }
    }
}
