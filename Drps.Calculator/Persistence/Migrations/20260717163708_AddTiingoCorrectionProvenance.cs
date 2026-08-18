using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTiingoCorrectionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasTiingoCorrectedClose",
                table: "RvolIndicators",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasTiingoCorrectedClose",
                table: "RsiIndicators",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasTiingoCorrectedClose",
                table: "DmaIndicators",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasTiingoCorrectedClose",
                table: "AtrIndicators",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasTiingoCorrectedClose",
                table: "RvolIndicators");

            migrationBuilder.DropColumn(
                name: "HasTiingoCorrectedClose",
                table: "RsiIndicators");

            migrationBuilder.DropColumn(
                name: "HasTiingoCorrectedClose",
                table: "DmaIndicators");

            migrationBuilder.DropColumn(
                name: "HasTiingoCorrectedClose",
                table: "AtrIndicators");
        }
    }
}
