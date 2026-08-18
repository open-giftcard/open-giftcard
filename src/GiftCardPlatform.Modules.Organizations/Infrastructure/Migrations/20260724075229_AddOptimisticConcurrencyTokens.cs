using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GiftCardPlatform.Modules.Organizations.Infrastructure.Migrations
{
    /// <summary>
    /// Maps <c>xmin</c> as an optimistic concurrency token (REVIEW-001, M5).
    ///
    /// Deliberately empty. <c>xmin</c> is a PostgreSQL **system column** present
    /// on every table, so there is nothing to create — the scaffolded
    /// <c>AddColumn</c> calls were removed because PostgreSQL rejects them with
    /// "column name xmin conflicts with a system column name".
    ///
    /// The migration is kept rather than deleted so the model snapshot stays in
    /// step with the configuration that introduced the shadow property.
    /// </summary>
    public partial class AddOptimisticConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally no schema change.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally no schema change.
        }
    }
}
