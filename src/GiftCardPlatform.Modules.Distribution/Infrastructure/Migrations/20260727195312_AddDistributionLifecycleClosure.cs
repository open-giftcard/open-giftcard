using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Distribution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributionLifecycleClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_invitations_state",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_invitations_state",
                schema: "distribution",
                table: "invitations",
                sql: "(\"state\" = 'Pending'\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL)\nOR\n(\"state\" = 'Claimed'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"identity_was_created_on_claim\" IS NOT NULL)\nOR\n(\"state\" IN ('Locked', 'Expired', 'Cancelled')\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL)");

            migrationBuilder.Sql(
                """
                create function
                    distribution.protect_terminal_invitation_state()
                returns trigger
                language plpgsql
                as $$
                begin
                    if old.state in (
                            'Claimed', 'Locked', 'Expired', 'Cancelled')
                       and new.state is distinct from old.state
                    then
                        raise exception
                            'terminal distribution invitation state is immutable'
                            using errcode = '55000';
                    end if;

                    return new;
                end;
                $$;

                create trigger
                    distribution_invitation_terminal_state_immutable
                    before update on distribution.invitations
                    for each row execute function
                        distribution.protect_terminal_invitation_state();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                do $$
                begin
                    if exists (
                        select 1
                        from distribution.invitations
                        where state = 'Cancelled'
                    )
                    then
                        raise exception
                            'cannot remove lifecycle closure while cancelled invitations exist'
                            using errcode = '55000';
                    end if;
                end;
                $$;

                drop function if exists
                    distribution.protect_terminal_invitation_state() cascade;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_distribution_invitations_state",
                schema: "distribution",
                table: "invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_distribution_invitations_state",
                schema: "distribution",
                table: "invitations",
                sql: "(\"state\" = 'Pending'\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL)\nOR\n(\"state\" = 'Claimed'\n    AND \"claimed_by_user_id\" IS NOT NULL\n    AND \"claimed_at_utc\" IS NOT NULL\n    AND \"claim_idempotency_key\" IS NOT NULL\n    AND \"identity_was_created_on_claim\" IS NOT NULL)\nOR\n(\"state\" IN ('Locked', 'Expired')\n    AND \"claimed_by_user_id\" IS NULL\n    AND \"claimed_at_utc\" IS NULL\n    AND \"claim_idempotency_key\" IS NULL\n    AND \"identity_was_created_on_claim\" IS NULL)");
        }
    }
}
