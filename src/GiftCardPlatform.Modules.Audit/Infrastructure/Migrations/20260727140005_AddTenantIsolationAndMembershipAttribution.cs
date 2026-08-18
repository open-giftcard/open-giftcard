using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Audit.Infrastructure.Migrations
{
    /// <summary>
    /// Adds membership attribution for customer actions and makes PostgreSQL
    /// RLS the authoritative read boundary for audit history.
    /// </summary>
    public partial class AddTenantIsolationAndMembershipAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "actor_membership_id",
                schema: "audit",
                table: "audit_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_actor_membership_id",
                schema: "audit",
                table: "audit_records",
                column: "actor_membership_id");

            migrationBuilder.Sql(
                """
                ALTER TABLE audit.audit_records ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit.audit_records FORCE ROW LEVEL SECURITY;

                CREATE POLICY audit_records_tenant_isolation
                    ON audit.audit_records
                    USING (
                        coalesce(
                            nullif(current_setting('app.is_platform_operator', true), ''),
                            'false')::boolean
                        OR (
                            organization_scope_id IS NOT NULL
                            AND organizations.organization_belongs_to_caller_tenant(
                                organization_scope_id)
                        )
                        OR (
                            organization_scope_id IS NULL
                            AND actor_user_id =
                                nullif(current_setting('app.user_id', true), '')::uuid
                        )
                    )
                    WITH CHECK (
                        coalesce(
                            nullif(current_setting('app.is_platform_operator', true), ''),
                            'false')::boolean
                        OR organization_scope_id IS NULL
                        OR organizations.organization_belongs_to_caller_tenant(
                            organization_scope_id)
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS audit_records_tenant_isolation
                    ON audit.audit_records;
                ALTER TABLE audit.audit_records DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropIndex(
                name: "ix_audit_records_actor_membership_id",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.DropColumn(
                name: "actor_membership_id",
                schema: "audit",
                table: "audit_records");
        }
    }
}
