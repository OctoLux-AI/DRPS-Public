using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGateScoreDataQualityFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasTiingoCorrectedData",
                table: "GateScores",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasUnverifiedPartialCreditData",
                table: "GateScores",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasTiingoCorrectedData",
                table: "GateScores");

            migrationBuilder.DropColumn(
                name: "HasUnverifiedPartialCreditData",
                table: "GateScores");
        }
    }
}
