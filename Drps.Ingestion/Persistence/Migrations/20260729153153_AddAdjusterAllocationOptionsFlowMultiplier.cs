using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdjusterAllocationOptionsFlowMultiplier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OptionsFlowMultiplierApplied",
                table: "AdjusterAllocations",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 1.0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptionsFlowMultiplierApplied",
                table: "AdjusterAllocations");
        }
    }
}
