using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneRecipientIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users_normalized_email",
                schema: "identity",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "normalized_email",
                schema: "identity",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                schema: "identity",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);

            migrationBuilder.AddColumn<string>(
                name: "normalized_phone_number",
                schema: "identity",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_number",
                schema: "identity",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_email",
                schema: "identity",
                table: "users",
                column: "normalized_email",
                unique: true,
                filter: "\"normalized_email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_phone",
                schema: "identity",
                table: "users",
                column: "normalized_phone_number",
                unique: true,
                filter: "\"normalized_phone_number\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_contact",
                schema: "identity",
                table: "users",
                sql: "(\"email\" IS NOT NULL\n    AND \"normalized_email\" IS NOT NULL\n    AND \"phone_number\" IS NULL\n    AND \"normalized_phone_number\" IS NULL)\nOR\n(\"email\" IS NULL\n    AND \"normalized_email\" IS NULL\n    AND \"phone_number\" IS NOT NULL\n    AND \"normalized_phone_number\" IS NOT NULL)");
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
                        from identity.users
                        where email is null
                           or normalized_email is null
                    ) then
                        raise exception
                            'Cannot remove phone recipient identities while phone-only users exist'
                            using errcode = '55000';
                    end if;
                end
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_users_normalized_email",
                schema: "identity",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_users_normalized_phone",
                schema: "identity",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_contact",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "normalized_phone_number",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone_number",
                schema: "identity",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "normalized_email",
                schema: "identity",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                schema: "identity",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_email",
                schema: "identity",
                table: "users",
                column: "normalized_email",
                unique: true);
        }
    }
}
