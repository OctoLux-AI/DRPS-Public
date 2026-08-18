using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRsiSlopeAndConcavityIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RsiConcavityIndicators",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BarDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SlopeLookback = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConfirmedDirection = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HasExDividendEvent = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    HasTiingoCorrectedClose = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    VerificationScopeLimited = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CalculationVersion = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RsiConcavityIndicators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RsiSlopeIndicators",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BarDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Lookback = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConfirmedDirection = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HasExDividendEvent = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    HasTiingoCorrectedClose = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    VerificationScopeLimited = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CalculationVersion = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RsiSlopeIndicators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RsiConcavityIndicators_Symbol_BarDate_SlopeLookback_CalculationVersion",
                table: "RsiConcavityIndicators",
                columns: new[] { "Symbol", "BarDate", "SlopeLookback", "CalculationVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RsiSlopeIndicators_Symbol_BarDate_Lookback_CalculationVersion",
                table: "RsiSlopeIndicators",
                columns: new[] { "Symbol", "BarDate", "Lookback", "CalculationVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RsiConcavityIndicators");

            migrationBuilder.DropTable(
                name: "RsiSlopeIndicators");
        }
    }
}
