using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGateScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GateScores",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScanDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Sector = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDmaAligned = table.Column<bool>(type: "bit", nullable: false),
                    IsDmaVerifiedAsync_Result = table.Column<bool>(type: "bit", nullable: false),
                    RsiValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RsiQuality = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RvolValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RvolQuality = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AtrValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AtrCleanPreEventValue = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    CompositeScore = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Bucket = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScoredThroughExDividendEvent = table.Column<bool>(type: "bit", nullable: false),
                    VerificationScopeLimited = table.Column<bool>(type: "bit", nullable: false),
                    CalculationVersion = table.Column<int>(type: "int", nullable: false),
                    GateParameterVersion = table.Column<long>(type: "bigint", nullable: false),
                    AllocationPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    NoBuyListExpiration = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GateScores_GateParameters_GateParameterVersion",
                        column: x => x.GateParameterVersion,
                        principalTable: "GateParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GateScores_GateParameterVersion",
                table: "GateScores",
                column: "GateParameterVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GateScores");
        }
    }
}
