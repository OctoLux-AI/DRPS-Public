using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGateScoreAllocationAddAdjusterAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllocationPercent",
                table: "GateScores");

            migrationBuilder.CreateTable(
                name: "AdjusterAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GateScoreId = table.Column<long>(type: "bigint", nullable: false),
                    AllocationPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AllocationDollarAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ShareCount = table.Column<long>(type: "bigint", nullable: false),
                    ShareCapDeficient = table.Column<bool>(type: "bit", nullable: false),
                    AsOfTimestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AdjusterParameterVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdjusterAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdjusterAllocations_GateScores_GateScoreId",
                        column: x => x.GateScoreId,
                        principalTable: "GateScores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdjusterAllocations_GateScoreId",
                table: "AdjusterAllocations",
                column: "GateScoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdjusterAllocations");

            migrationBuilder.AddColumn<decimal>(
                name: "AllocationPercent",
                table: "GateScores",
                type: "decimal(18,4)",
                nullable: true);
        }
    }
}
