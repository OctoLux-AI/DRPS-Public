using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    GateScoreId = table.Column<long>(type: "bigint", nullable: false),
                    AdjusterAllocationId = table.Column<long>(type: "bigint", nullable: false),
                    EntryDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    EntryQuantity = table.Column<decimal>(type: "decimal(18,9)", nullable: false),
                    ExitDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ExitQuantity = table.Column<decimal>(type: "decimal(18,9)", nullable: true),
                    ExitReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LowGradeDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PlateauDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReactivatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeactivatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CoolDownStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ShareCapDeficient = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Positions_AdjusterAllocations_AdjusterAllocationId",
                        column: x => x.AdjusterAllocationId,
                        principalTable: "AdjusterAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Positions_GateScores_GateScoreId",
                        column: x => x.GateScoreId,
                        principalTable: "GateScores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_AdjusterAllocationId",
                table: "Positions",
                column: "AdjusterAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_GateScoreId",
                table: "Positions",
                column: "GateScoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Positions");
        }
    }
}
