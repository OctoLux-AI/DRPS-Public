using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTickerSourceOriginToDmaIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not
            // a Swap" (2026-07-30) - added NULLABLE here, deliberately NOT with an inline
            // AddColumn defaultValue. EF's own scaffolder auto-selected defaultValue: ""
            // (empty string) for this NOT NULL column when the migration was first generated -
            // hand-edited out, same precedent as AddConsecutiveLossCircuitBreakerAndPositionActionOrigin's
            // OpenOrigin column, since "" is not a real, documented TickerSourceOrigin value.
            // Unlike that precedent's Unknown backfill, every row that predates this column is
            // KNOWN, not merely assumed, to have come from the Watchlist alone - before this
            // change, Worker.cs's per-symbol loop had exactly one ticker source
            // (WatchlistOptions.Symbols), so 'Watchlist' is the accurate historical value here,
            // not a guessed placeholder.
            migrationBuilder.AddColumn<string>(
                name: "TickerSourceOrigin",
                table: "DmaIndicators",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql("UPDATE [DmaIndicators] SET [TickerSourceOrigin] = 'Watchlist' WHERE [TickerSourceOrigin] IS NULL;");

            // Now that every row has a real value, tighten to NOT NULL to match the model
            // (DmaIndicator.TickerSourceOrigin is a non-nullable TickerSourceOrigin).
            migrationBuilder.AlterColumn<string>(
                name: "TickerSourceOrigin",
                table: "DmaIndicators",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TickerSourceOrigin",
                table: "DmaIndicators");
        }
    }
}
