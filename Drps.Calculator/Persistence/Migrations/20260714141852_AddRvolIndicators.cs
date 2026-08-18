using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRvolIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RvolIndicators",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BarDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BaselineWindow = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    HasExDividendEvent = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CalculationVersion = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RvolIndicators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RvolIndicators_Symbol_BarDate_BaselineWindow_CalculationVersion",
                table: "RvolIndicators",
                columns: new[] { "Symbol", "BarDate", "BaselineWindow", "CalculationVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RvolIndicators");
        }
    }
}
