using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionTickerOpenUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Positions_Ticker",
                table: "Positions",
                column: "Ticker",
                unique: true,
                filter: "[ExitDate] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Positions_Ticker",
                table: "Positions");
        }
    }
}
