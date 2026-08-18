using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Authorization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAuthorizationBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_system",
                schema: "authorization",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "organization_administrator_bootstraps",
                schema: "authorization",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_administrator_bootstraps", x => x.organization_id);
                    table.ForeignKey(
                        name: "FK_organization_administrator_bootstraps_membership_role_assig~",
                        column: x => x.role_assignment_id,
                        principalSchema: "authorization",
                        principalTable: "membership_role_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_administrator_bootstraps_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "authorization",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_bootstrap_state",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_bootstrap_state", x => x.id);
                    table.CheckConstraint("ck_platform_bootstrap_state_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "platform_roles",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_role_assignments",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_role_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_platform_role_assignments_platform_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "authorization",
                        principalTable: "platform_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_role_permissions",
                schema: "authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_role_permissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_platform_role_permissions_permissions_permission",
                        column: x => x.permission,
                        principalSchema: "authorization",
                        principalTable: "permissions",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_platform_role_permissions_platform_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "authorization",
                        principalTable: "platform_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "authorization",
                table: "platform_bootstrap_state",
                columns: new[] { "id", "completed_at_utc", "completed_by_user_id" },
                values: new object[] { 1, null, null });

            migrationBuilder.CreateIndex(
                name: "ux_organization_admin_bootstraps_assignment",
                schema: "authorization",
                table: "organization_administrator_bootstraps",
                column: "role_assignment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organization_admin_bootstraps_membership",
                schema: "authorization",
                table: "organization_administrator_bootstraps",
                column: "membership_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organization_admin_bootstraps_role",
                schema: "authorization",
                table: "organization_administrator_bootstraps",
                column: "role_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_role_assignments_role_id",
                schema: "authorization",
                table: "platform_role_assignments",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_role_assignments_user_id",
                schema: "authorization",
                table: "platform_role_assignments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_platform_role_assignments_user_role",
                schema: "authorization",
                table: "platform_role_assignments",
                columns: new[] { "user_id", "role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_role_permissions_permission",
                schema: "authorization",
                table: "platform_role_permissions",
                column: "permission");

            migrationBuilder.CreateIndex(
                name: "ux_platform_role_permissions_role_permission",
                schema: "authorization",
                table: "platform_role_permissions",
                columns: new[] { "role_id", "permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_platform_roles_name",
                schema: "authorization",
                table: "platform_roles",
                column: "name",
                unique: true);

            // Ordinary platform context remains read-only for tenant-owned
            // authorization rows. The one initial-administrator service enables
            // this transaction-local flag only after checking its named platform
            // permission; raw platform context without the flag still fails.
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
                    DROP POLICY IF EXISTS {table}_tenant_isolation
                        ON "authorization".{table};

                    CREATE POLICY {table}_tenant_isolation
                        ON "authorization".{table}
                        USING (
                            organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                            OR coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        )
                        WITH CHECK (
                            organization_id = nullif(current_setting('app.organization_id', true), '')::uuid
                            OR (
                                coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                                AND coalesce(nullif(current_setting('app.is_initial_admin_bootstrap', true), ''), 'false')::boolean
                            )
                        );
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                    DROP POLICY IF EXISTS {table}_tenant_isolation
                        ON "authorization".{table};

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

            migrationBuilder.DropTable(
                name: "organization_administrator_bootstraps",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "platform_bootstrap_state",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "platform_role_assignments",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "platform_role_permissions",
                schema: "authorization");

            migrationBuilder.DropTable(
                name: "platform_roles",
                schema: "authorization");

            migrationBuilder.DropColumn(
                name: "is_system",
                schema: "authorization",
                table: "roles");
        }
    }
}
