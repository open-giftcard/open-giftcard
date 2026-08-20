using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Audit.Infrastructure.Migrations
{
    /// <summary>
    /// Lets a POS device attribute an audit record to the tenant whose card it is
    /// holding, rather than only to a tenant it has already written a provision
    /// row for.
    ///
    /// The existing proof, <c>caller_pos_client_holds_scope</c>, asks whether the
    /// calling client already has a provision funded by the target organization.
    /// That works for the operations it was written for, because the provision row
    /// is inserted before the audit record. It cannot work for any POS operation
    /// that reserves nothing: a balance inquiry writes no provision, so on first
    /// contact with an organization there is nothing for the function to find and
    /// the audit write is refused with 42501. Every future POS operation that does
    /// not reserve value would meet the same wall.
    ///
    /// This is deliberately additive. The provision-based proof is kept exactly as
    /// it was, so nothing that passes today stops passing, and the alternative is
    /// the organization the device's presented credential resolves to, published
    /// as a transaction-local setting by the application at the moment the card is
    /// identified. That is the same trust model the entire tenant boundary already
    /// rests on: app.user_id and app.organization_id are set the same way, from
    /// verified server state, by the same writer, inside the same transaction.
    /// </summary>
    public partial class AllowPosCredentialScopedAuditWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                drop policy if exists audit_records_pos_provision_write
                    on audit.audit_records;

                -- INSERT only, as before. A till never reads audit history, so
                -- admitting the write grants no visibility into the tenant.
                create policy audit_records_pos_provision_write
                    on audit.audit_records
                    for insert
                    with check (
                        organization_scope_id is not null
                        and (
                            audit.caller_pos_client_holds_scope(organization_scope_id)
                            or organization_scope_id = nullif(
                                current_setting('app.pos_credential_organization_id', true),
                                '')::uuid
                        )
                    );
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                drop policy if exists audit_records_pos_provision_write
                    on audit.audit_records;

                create policy audit_records_pos_provision_write
                    on audit.audit_records
                    for insert
                    with check (
                        organization_scope_id is not null
                        and audit.caller_pos_client_holds_scope(organization_scope_id)
                    );
                """);
    }
}
