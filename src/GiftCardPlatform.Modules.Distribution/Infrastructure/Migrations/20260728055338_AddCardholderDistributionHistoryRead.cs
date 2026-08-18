using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Distribution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCardholderDistributionHistoryRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create policy distribution_events_cardholder_history_read
                    on distribution.events
                    for select
                    using (
                        exists (
                            select 1
                            from distribution.invitations invitation
                            where invitation.id =
                                distribution.events.invitation_id
                              and invitation.claimed_by_user_id =
                                  nullif(
                                      current_setting('app.user_id', true),
                                      '')::uuid
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop policy if exists
                    distribution_events_cardholder_history_read
                    on distribution.events;
                """);
        }
    }
}
