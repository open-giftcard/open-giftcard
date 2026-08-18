using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTokenCandidatePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            // A POS principal holds no user, membership, organization, or tenant
            // scope, so the issuance-time policy correctly hides every credential
            // from it. Creating a provision still has to read and consume the one
            // credential presented at the till.
            //
            // This is the narrow-candidate pattern already used for distribution
            // claim and share claim: the server parses only the identifier out of
            // the presented credential, sets it as a transaction-local candidate,
            // and admits exactly that row. The identifier alone grants nothing,
            // because the 256-bit secret is still verified in constant time before
            // any value is reserved (ADR-017).
            migrationBuilder.Sql(
                """
                create policy payments_tokens_candidate on payments.payment_tokens
                    using (id = nullif(current_setting('app.payment_token_id', true), '')::uuid)
                    with check (id = nullif(current_setting('app.payment_token_id', true), '')::uuid);
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "drop policy if exists payments_tokens_candidate on payments.payment_tokens;");
    }
}
