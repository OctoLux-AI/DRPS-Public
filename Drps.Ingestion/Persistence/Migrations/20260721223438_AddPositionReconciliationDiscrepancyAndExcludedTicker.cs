using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionReconciliationDiscrepancyAndExcludedTicker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExcludedTickers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcludedTickers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PositionReconciliationDiscrepancies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PositionId = table.Column<long>(type: "bigint", nullable: false),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DetectedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LedgerQuantity = table.Column<decimal>(type: "decimal(18,9)", nullable: false),
                    AlpacaQuantity = table.Column<decimal>(type: "decimal(18,9)", nullable: false),
                    LedgerPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AlpacaAverageEntryPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionReconciliationDiscrepancies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionReconciliationDiscrepancies_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExcludedTickers_Ticker",
                table: "ExcludedTickers",
                column: "Ticker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PositionReconciliationDiscrepancies_PositionId",
                table: "PositionReconciliationDiscrepancies",
                column: "PositionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExcludedTickers");

            migrationBuilder.DropTable(
                name: "PositionReconciliationDiscrepancies");
        }
    }
}
