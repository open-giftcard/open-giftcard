using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPosProvisionAuditPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                -- A POS principal holds no user, membership, organization, or
                -- tenant scope, so the tenant write check refuses its audit rows.
                -- Recording them with a null scope would hide a till's holds from
                -- the customer whose card was held, so the scope stays real and
                -- the write path is narrowed instead.
                --
                -- The rule is exactly: a till may record audit only in a tenant
                -- where it actually holds a provision. Creation writes its audit
                -- after the provision row exists in the same transaction, and
                -- cancellation reads an existing one, so one predicate covers
                -- both without admitting anything else.
                --
                -- plpgsql and tolerant of a missing table for the same reason the
                -- Gift Cards and Ledger payment helpers are: module migrations
                -- run in a fixed order and Payments migrates after Audit, so the
                -- referenced table does not exist yet when this runs.
                create function audit.caller_pos_client_holds_scope(
                    target_organization_id uuid)
                returns boolean
                language plpgsql
                stable
                security invoker
                set search_path = pg_catalog, payments
                as $$
                begin
                    return exists (
                        select 1
                        from payments.payment_provisions provision
                        where provision.pos_client_id =
                                  nullif(current_setting('app.pos_client_id', true), '')::uuid
                          and provision.funding_organization_id = target_organization_id
                    );
                exception
                    when undefined_table then return false;
                end;
                $$;

                -- INSERT only. A till never reads audit history, so admitting the
                -- write grants no visibility of anything else in that tenant.
                create policy audit_records_pos_provision_write
                    on audit.audit_records
                    for insert
                    with check (
                        organization_scope_id is not null
                        and audit.caller_pos_client_holds_scope(organization_scope_id)
                    );
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                drop policy if exists audit_records_pos_provision_write on audit.audit_records;
                drop function if exists audit.caller_pos_client_holds_scope(uuid);
                """);
    }
}
