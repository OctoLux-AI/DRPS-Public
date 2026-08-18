using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationScopeLimitedToRsiAndAtrIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VerificationScopeLimited",
                table: "RsiIndicators",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "VerificationScopeLimited",
                table: "AtrIndicators",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationScopeLimited",
                table: "RsiIndicators");

            migrationBuilder.DropColumn(
                name: "VerificationScopeLimited",
                table: "AtrIndicators");
        }
    }
}
