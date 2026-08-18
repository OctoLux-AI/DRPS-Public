using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropPositionShareCapDeficient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShareCapDeficient",
                table: "Positions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShareCapDeficient",
                table: "Positions",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
