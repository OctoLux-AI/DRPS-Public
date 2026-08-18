using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionEntryAtrAndTpTargetPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EntryAtr",
                table: "Positions",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TpTargetPrice",
                table: "Positions",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryAtr",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "TpTargetPrice",
                table: "Positions");
        }
    }
}
