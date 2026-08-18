using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPosClientsAndTerminals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pos_clients",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pos_clients", x => x.id);
                    table.CheckConstraint("ck_pos_clients_disabled", "(\"status\" = 'Disabled') = (\"disabled_at_utc\" IS NOT NULL)");
                    table.CheckConstraint("ck_pos_clients_secret_hash", "\"secret_hash\" ~ '^[0-9A-F]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "pos_terminals",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pos_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    store_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pos_terminals", x => x.id);
                    table.CheckConstraint("ck_pos_terminals_disabled", "(\"status\" = 'Disabled') = (\"disabled_at_utc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_pos_terminals_pos_clients_pos_client_id",
                        column: x => x.pos_client_id,
                        principalSchema: "payments",
                        principalTable: "pos_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pos_clients_code",
                schema: "payments",
                table: "pos_clients",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pos_terminals_client_code",
                schema: "payments",
                table: "pos_terminals",
                columns: new[] { "pos_client_id", "code" },
                unique: true);

            migrationBuilder.Sql(
                """
                -- These two tables deliberately carry no Row-Level Security, for
                -- the same reason the platform-role tables do not (ADR-021): they
                -- are platform-scoped, not tenant-owned. The platform operator owns the
                -- stores, so no customer organization owns a till, and there is
                -- no organization_id to isolate on.
                --
                -- The credential exchange must also read them before any caller
                -- is authenticated, so a policy requiring verified session
                -- context would make authentication impossible. Only the secret
                -- hash is stored, exactly as identity refresh credentials are,
                -- and the runtime role cannot delete either table's rows.
                revoke delete on payments.pos_clients from public;
                revoke delete on payments.pos_terminals from public;

                -- A registered client's identity and code are fixed; only its
                -- status, secret, and disabled stamp may move. Rewriting a code
                -- would silently repoint every till that authenticates with it.
                create function payments.protect_pos_client_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.code is distinct from old.code
                       or new.registered_at_utc is distinct from old.registered_at_utc
                    then
                        raise exception 'pos client identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger pos_clients_identity_immutable
                    before update on payments.pos_clients
                    for each row execute function payments.protect_pos_client_identity();

                create function payments.protect_pos_terminal_identity()
                returns trigger language plpgsql as $$
                begin
                    if new.id is distinct from old.id
                       or new.pos_client_id is distinct from old.pos_client_id
                       or new.code is distinct from old.code
                       or new.store_reference is distinct from old.store_reference
                       or new.registered_at_utc is distinct from old.registered_at_utc
                    then
                        raise exception 'pos terminal identity is immutable' using errcode = '55000';
                    end if;
                    return new;
                end;
                $$;

                create trigger pos_terminals_identity_immutable
                    before update on payments.pos_terminals
                    for each row execute function payments.protect_pos_terminal_identity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger if exists pos_terminals_identity_immutable on payments.pos_terminals;
                drop trigger if exists pos_clients_identity_immutable on payments.pos_clients;
                drop function if exists payments.protect_pos_terminal_identity();
                drop function if exists payments.protect_pos_client_identity();
                """);

            migrationBuilder.DropTable(
                name: "pos_terminals",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "pos_clients",
                schema: "payments");
        }
    }
}
