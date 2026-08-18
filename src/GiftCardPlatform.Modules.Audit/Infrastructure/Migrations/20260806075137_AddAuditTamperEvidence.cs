using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTamperEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "audit_record_sequence",
                schema: "audit");

            // Existing installations may not yet have the sequence default
            // privilege introduced with this slice. Mirror every role already
            // allowed to append audit records without hard-coding an
            // environment-specific runtime role name.
            migrationBuilder.Sql(
                """
                DO $grant_sequence$
                DECLARE
                    runtime_role name;
                BEGIN
                    FOR runtime_role IN
                        SELECT DISTINCT grantee
                        FROM information_schema.role_table_grants
                        WHERE table_schema = 'audit'
                          AND table_name = 'audit_records'
                          AND privilege_type = 'INSERT'
                    LOOP
                        EXECUTE format(
                            'GRANT USAGE, SELECT ON SEQUENCE audit.audit_record_sequence TO %I',
                            runtime_role);
                    END LOOP;
                END
                $grant_sequence$;
                """);

            migrationBuilder.AddColumn<long>(
                name: "audit_sequence",
                schema: "audit",
                table: "audit_records",
                type: "bigint",
                nullable: false,
                defaultValueSql: "nextval('audit.audit_record_sequence')");

            migrationBuilder.CreateTable(
                name: "audit_checkpoints",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_checkpoint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_manifest_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    first_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    merkle_root = table.Column<byte[]>(type: "bytea", nullable: false),
                    manifest_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    hash_algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_checkpoints", x => x.id);
                    table.CheckConstraint("ck_audit_checkpoint_digest_length", "octet_length(manifest_digest) = 32");
                    table.CheckConstraint("ck_audit_checkpoint_previous_digest_length", "previous_manifest_digest IS NULL OR octet_length(previous_manifest_digest) = 32");
                    table.CheckConstraint("ck_audit_checkpoint_record_count", "record_count > 0");
                    table.CheckConstraint("ck_audit_checkpoint_root_length", "octet_length(merkle_root) = 32");
                    table.CheckConstraint("ck_audit_checkpoint_sequence", "first_sequence > 0 AND last_sequence >= first_sequence");
                });

            migrationBuilder.CreateTable(
                name: "audit_checkpoint_seals",
                schema: "audit",
                columns: table => new
                {
                    checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    signed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_checkpoint_seals", x => x.checkpoint_id);
                    table.CheckConstraint("ck_audit_checkpoint_public_key", "octet_length(public_key) > 0");
                    table.CheckConstraint("ck_audit_checkpoint_signature", "octet_length(signature) = 64");
                    table.ForeignKey(
                        name: "FK_audit_checkpoint_seals_audit_checkpoints_checkpoint_id",
                        column: x => x.checkpoint_id,
                        principalSchema: "audit",
                        principalTable: "audit_checkpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_checkpoint_witnesses",
                schema: "audit",
                columns: table => new
                {
                    checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    manifest_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    witnessed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_checkpoint_witnesses", x => x.checkpoint_id);
                    table.CheckConstraint("ck_audit_witness_digest_length", "octet_length(manifest_digest) = 32");
                    table.ForeignKey(
                        name: "FK_audit_checkpoint_witnesses_audit_checkpoints_checkpoint_id",
                        column: x => x.checkpoint_id,
                        principalSchema: "audit",
                        principalTable: "audit_checkpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_audit_records_sequence",
                schema: "audit",
                table: "audit_records",
                column: "audit_sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_audit_checkpoint_witness_reference",
                schema: "audit",
                table: "audit_checkpoint_witnesses",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_audit_checkpoints_last_sequence",
                schema: "audit",
                table: "audit_checkpoints",
                column: "last_sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_audit_checkpoints_manifest_digest",
                schema: "audit",
                table: "audit_checkpoints",
                column: "manifest_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_audit_checkpoints_previous_id",
                schema: "audit",
                table: "audit_checkpoints",
                column: "previous_checkpoint_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_checkpoint_seals",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit_checkpoint_witnesses",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit_checkpoints",
                schema: "audit");

            migrationBuilder.DropIndex(
                name: "ux_audit_records_sequence",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.DropColumn(
                name: "audit_sequence",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.DropSequence(
                name: "audit_record_sequence",
                schema: "audit");
        }
    }
}
