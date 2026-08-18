using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "authorization");

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "authorization",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_platform_permission = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "membership_role_assignments",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    anchor_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_role_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_membership_role_assignments_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "authorization",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission",
                        column: x => x.permission,
                        principalSchema: "authorization",
                        principalTable: "permissions",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "authorization",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "membership_role_assignment_scopes",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_role_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_role_assignment_scopes", x => x.id);
                    table.ForeignKey(
                        name: "FK_membership_role_assignment_scopes_membership_role_assignmen~",
                        column: x => x.membership_role_assignment_id,
                        principalSchema: "authorization",
                        principalTable: "membership_role_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_assignment_scopes_assignment_organization",
                schema: "authorization",
                table: "membership_role_assignment_scopes",
                columns: new[] { "membership_role_assignment_id", "granted_organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_membership_role_assignments_membership_id",
                schema: "authorization",
                table: "membership_role_assignments",
                column: "membership_id");

            migrationBuilder.CreateIndex(
                name: "IX_membership_role_assignments_role_id",
                schema: "authorization",
                table: "membership_role_assignments",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_membership_role_assignments_membership_role_anchor",
                schema: "authorization",
                table: "membership_role_assignments",
                columns: new[] { "membership_id", "role_id", "anchor_organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission",
                schema: "authorization",
                table: "role_permissions",
                column: "permission");

            migrationBuilder.CreateIndex(
                name: "ux_role_permissions_role_permission",
                schema: "authorization",
                table: "role_permissions",
                columns: new[] { "role_id", "permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_roles_organization_name",
                schema: "authorization",
                table: "roles",
                columns: new[] { "organization_id", "name" },
                unique: true);

            // Row-Level Security on every tenant-owned authorization table
            // (ADR-005). The permissions catalogue is deliberately excluded: it
            // is a global definition table with no organization_id.
            //
            // The tenant key sits directly on each row — denormalized onto the
            // grant and scope tables — so the policy needs no join and no
            // SECURITY DEFINER lookup. That is why FORCE is used here, unlike the
            // organizations table (ADR-023): nothing depends on the owner being
            // exempt, so the owner is subject too.
            //
            // USING admits the caller's own organization or a platform operator,
            // giving support read access across tenants. WITH CHECK omits the
            // platform path, so no caller can write authorization rows into an
            // organization other than its own. Roles and grants decide who may do
            // what; letting a platform operator write them silently would be a
            // privilege-escalation path.
            foreach (var table in new[]
                     {
                         "roles",
                         "role_permissions",
                         "membership_role_assignments",
                         "membership_role_assignment_scopes",
                     })
            {
                migrationBuilder.Sql(
                    $"""
                    ALTER TABLE "authorization".{table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "authorization".{table} FORCE ROW LEVEL SECURITY;

                    CREATE POLICY {table}_tenant_isolation
                        ON "authorization".{table}
                        USING (
                            organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                            OR coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        )
                        WITH CHECK (
                            organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                        );
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_role_assignment_scopes",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "membership_role_assignments",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "authorization");
        }
    }
}
