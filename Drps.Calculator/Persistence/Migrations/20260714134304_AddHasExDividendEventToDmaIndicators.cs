using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHasExDividendEventToDmaIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasExDividendEvent",
                table: "DmaIndicators",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasExDividendEvent",
                table: "DmaIndicators");
        }
    }
}
