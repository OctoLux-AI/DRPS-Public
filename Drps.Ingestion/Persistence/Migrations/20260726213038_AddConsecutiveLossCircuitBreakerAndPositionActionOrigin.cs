using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConsecutiveLossCircuitBreakerAndPositionActionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloseOrigin",
                table: "Positions",
                type: "nvarchar(max)",
                nullable: true);

            // CLAUDE.md's Execution Layer: Tenth Design Decision - OpenOrigin is added NULLABLE
            // here, deliberately NOT with an inline AddColumn defaultValue. EF's own scaffolder
            // auto-selected defaultValue: "" (empty string) for this NOT NULL column when the
            // migration was first generated - hand-edited out, since "" is not a real,
            // documented PositionActionOrigin value and would have silently misrepresented every
            // pre-existing row. The explicit backfill below sets the real, intended value
            // ('Unknown') before the column is tightened to NOT NULL, matching
            // PositionConfiguration.cs's own deliberate choice not to carry a permanent
            // column-level default forward for ongoing inserts.
            migrationBuilder.AddColumn<string>(
                name: "OpenOrigin",
                table: "Positions",
                type: "nvarchar(max)",
                nullable: true);

            // One-time backfill for every row that predates this column - Unknown is a
            // permanent, legitimate value for these historical rows (never guessed from the
            // EntryAtr/TpTargetPrice/HighWaterMark null-ness correlate the same design decision
            // already flagged as non-authoritative).
            migrationBuilder.Sql("UPDATE [Positions] SET [OpenOrigin] = 'Unknown' WHERE [OpenOrigin] IS NULL;");

            // Already-closed rows predating this column get CloseOrigin = 'Unknown' too, per the
            // Tenth Design Decision's backfill note. Still-open rows keep CloseOrigin = NULL,
            // matching the existing null-until-closed convention (ExitDate/ExitPrice/etc.).
            migrationBuilder.Sql("UPDATE [Positions] SET [CloseOrigin] = 'Unknown' WHERE [ExitDate] IS NOT NULL AND [CloseOrigin] IS NULL;");

            // Now that every row has a real value, tighten OpenOrigin to NOT NULL to match the
            // model (Position.OpenOrigin is a non-nullable PositionActionOrigin).
            migrationBuilder.AlterColumn<string>(
                name: "OpenOrigin",
                table: "Positions",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "ConsecutiveLossCircuitBreakers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsecutiveLossCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Tripped = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    TrippedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastEvaluatedPositionId = table.Column<long>(type: "bigint", nullable: true),
                    ResetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsecutiveLossCircuitBreakers", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsecutiveLossCircuitBreakers");

            migrationBuilder.DropColumn(
                name: "CloseOrigin",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "OpenOrigin",
                table: "Positions");
        }
    }
}
