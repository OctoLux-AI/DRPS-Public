using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionHighWaterMarkAndTpProgressAtExit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HighWaterMark",
                table: "Positions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TpProgressAtExit",
                table: "Positions",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighWaterMark",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "TpProgressAtExit",
                table: "Positions");
        }
    }
}
