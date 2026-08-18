using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdjusterAllocationInsiderMultiplier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InsiderDataUnverified",
                table: "AdjusterAllocations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "InsiderMultiplierApplied",
                table: "AdjusterAllocations",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 1.0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InsiderDataUnverified",
                table: "AdjusterAllocations");

            migrationBuilder.DropColumn(
                name: "InsiderMultiplierApplied",
                table: "AdjusterAllocations");
        }
    }
}
