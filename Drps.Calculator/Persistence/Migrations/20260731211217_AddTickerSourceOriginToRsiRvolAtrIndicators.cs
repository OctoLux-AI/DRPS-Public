using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTickerSourceOriginToRsiRvolAtrIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not
            // a Swap" (2026-07-30) - same hand-correction as
            // AddTickerSourceOriginToDmaIndicators: added NULLABLE, deliberately NOT with an
            // inline AddColumn defaultValue. EF's own scaffolder auto-selected defaultValue: ""
            // (empty string) for these three NOT NULL columns when the migration was first
            // generated - hand-edited out, since "" is not a real, documented
            // TickerSourceOrigin value. Every row that predates this column is KNOWN, not
            // merely assumed, to have come from the Watchlist alone - before this task, only
            // DmaIndicator carried origin; RsiIndicator/RvolIndicator/AtrIndicator had no such
            // column at all, and every row ever computed came from the same single Watchlist
            // source that predates the whole discovered-aligned-union feature - so 'Watchlist'
            // is the accurate historical value here, not a guessed placeholder.
            migrationBuilder.AddColumn<string>(
                name: "TickerSourceOrigin",
                table: "RvolIndicators",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TickerSourceOrigin",
                table: "RsiIndicators",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TickerSourceOrigin",
                table: "AtrIndicators",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql("UPDATE [RvolIndicators] SET [TickerSourceOrigin] = 'Watchlist' WHERE [TickerSourceOrigin] IS NULL;");
            migrationBuilder.Sql("UPDATE [RsiIndicators] SET [TickerSourceOrigin] = 'Watchlist' WHERE [TickerSourceOrigin] IS NULL;");
            migrationBuilder.Sql("UPDATE [AtrIndicators] SET [TickerSourceOrigin] = 'Watchlist' WHERE [TickerSourceOrigin] IS NULL;");

            // Now that every row has a real value, tighten to NOT NULL to match the model
            // (RsiIndicator/RvolIndicator/AtrIndicator.TickerSourceOrigin are all non-nullable
            // TickerSourceOrigin).
            migrationBuilder.AlterColumn<string>(
                name: "TickerSourceOrigin",
                table: "RvolIndicators",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TickerSourceOrigin",
                table: "RsiIndicators",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TickerSourceOrigin",
                table: "AtrIndicators",
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
                table: "RvolIndicators");

            migrationBuilder.DropColumn(
                name: "TickerSourceOrigin",
                table: "RsiIndicators");

            migrationBuilder.DropColumn(
                name: "TickerSourceOrigin",
                table: "AtrIndicators");
        }
    }
}
