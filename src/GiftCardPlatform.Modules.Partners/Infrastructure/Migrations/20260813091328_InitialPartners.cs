using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Partners.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPartners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "partners");

            migrationBuilder.CreateTable(
                name: "partners",
                schema: "partners",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_partners", x => x.id);
                    table.CheckConstraint("ck_partners_code", "\"code\" ~ '^[A-Z0-9-]+$'");
                    table.CheckConstraint("ck_partners_status", "(\"status\" = 'Active' AND \"disabled_at_utc\" IS NULL)\nOR\n(\"status\" = 'Disabled' AND \"disabled_at_utc\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "api_clients",
                schema: "partners",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    secret_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_clients", x => x.id);
                    table.CheckConstraint("ck_partner_api_clients_code", "\"code\" ~ '^[A-Z0-9-]+$'");
                    table.CheckConstraint("ck_partner_api_clients_secret_hash", "\"secret_hash\" ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_partner_api_clients_status", "(\"status\" = 'Active' AND \"disabled_at_utc\" IS NULL)\nOR\n(\"status\" = 'Disabled' AND \"disabled_at_utc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_api_clients_partners_partner_id",
                        column: x => x.partner_id,
                        principalSchema: "partners",
                        principalTable: "partners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_partner_api_clients_partner",
                schema: "partners",
                table: "api_clients",
                column: "partner_id");

            migrationBuilder.CreateIndex(
                name: "ux_partner_api_clients_code",
                schema: "partners",
                table: "api_clients",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_partners_code",
                schema: "partners",
                table: "partners",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_partners_root_organization",
                schema: "partners",
                table: "partners",
                column: "root_organization_id",
                unique: true);

            migrationBuilder.Sql(
                """
                -- Both tables are tenant-owned: a partner is anchored to the root
                -- organization whose prepaid corporate credit funds every card it
                -- mints, so they carry forced RLS from this first migration
                -- (ADR-005, ADR-019). This is the difference from payments.pos_clients,
                -- which has no policy only because the platform operator owns the tills and there is
                -- no organization to isolate on.
                alter table partners.partners enable row level security;
                alter table partners.partners force row level security;
                alter table partners.api_clients enable row level security;
                alter table partners.api_clients force row level security;

                -- The credential exchange must read these rows before any caller is
                -- authenticated, which a purely tenant-scoped policy would make
                -- impossible. Rather than dropping RLS for that one path, it gets a
                -- narrow, explicitly flagged escape on an independently scoped
                -- connection, the same device the initial-administrator bootstrap
                -- uses via app.is_initial_admin_bootstrap.
                --
                -- The escape is READ ONLY: it appears in `using` and deliberately not
                -- in `with check`, so an anonymous credential lookup can never insert
                -- or modify a partner or a key. Nothing sets this flag yet; until the
                -- credential exchange lands, current_setting returns NULL and the
                -- clause is false, so the tables are tenant-isolated outright.
                create policy partners_partners_isolation on partners.partners
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(root_organization_id)
                        or coalesce(nullif(current_setting('app.is_partner_credential_lookup', true), ''), 'false')::boolean
                    )
                    with check (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(root_organization_id)
                    );

                create policy partners_api_clients_isolation on partners.api_clients
                    using (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(root_organization_id)
                        or coalesce(nullif(current_setting('app.is_partner_credential_lookup', true), ''), 'false')::boolean
                    )
                    with check (
                        coalesce(nullif(current_setting('app.is_platform_operator', true), ''), 'false')::boolean
                        or organizations.organization_belongs_to_caller_tenant(root_organization_id)
                    );

                -- A retired credential is evidence, and a partner row anchors the
                -- funding tenant of every card it ever minted. Neither is deletable
                -- by the runtime role; disabling is the supported retirement.
                revoke delete on partners.partners from public;
                revoke delete on partners.api_clients from public;

                -- A partner's identity, funding tenant, and code are fixed. Repointing
                -- root_organization_id would silently move future minting onto another
                -- company's money; rewriting a code would repoint a live integration.
                create function partners.protect_partner_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.root_organization_id is distinct from old.root_organization_id
                       or new.code is distinct from old.code
                       or new.registered_at_utc is distinct from old.registered_at_utc
                    then
                        raise exception 'partner identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger partners_identity_immutable
                    before update on partners.partners
                    for each row execute function partners.protect_partner_identity();

                create function partners.protect_partner_api_client_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.partner_id is distinct from old.partner_id
                       or new.root_organization_id is distinct from old.root_organization_id
                       or new.code is distinct from old.code
                       or new.registered_at_utc is distinct from old.registered_at_utc
                    then
                        raise exception 'partner api client identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger partner_api_clients_identity_immutable
                    before update on partners.api_clients
                    for each row execute function partners.protect_partner_api_client_identity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the tables removes their policies and triggers, but the
            // trigger functions are schema-level and outlive them.
            migrationBuilder.Sql(
                """
                drop trigger if exists partner_api_clients_identity_immutable on partners.api_clients;
                drop trigger if exists partners_identity_immutable on partners.partners;
                drop function if exists partners.protect_partner_api_client_identity();
                drop function if exists partners.protect_partner_identity();
                """);

            migrationBuilder.DropTable(
                name: "api_clients",
                schema: "partners");

            migrationBuilder.DropTable(
                name: "partners",
                schema: "partners");
        }
    }
}
