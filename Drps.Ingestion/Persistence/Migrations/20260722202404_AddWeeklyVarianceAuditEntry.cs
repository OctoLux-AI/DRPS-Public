using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyVarianceAuditEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeeklyVarianceAuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BarDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AlpacaValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TiingoValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AbsoluteVariance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PercentVariance = table.Column<decimal>(type: "decimal(9,6)", nullable: false),
                    WeekEndingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyVarianceAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyVarianceAuditEntries_Ticker_Field_WeekEndingDate",
                table: "WeeklyVarianceAuditEntries",
                columns: new[] { "Ticker", "Field", "WeekEndingDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklyVarianceAuditEntries");
        }
    }
}
