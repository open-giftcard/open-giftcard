using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditInvestigationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_records_organization_history",
                schema: "audit",
                table: "audit_records",
                columns: new[] { "organization_scope_id", "occurred_at_utc", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_records_organization_history",
                schema: "audit",
                table: "audit_records");
        }
    }
}
