using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Calculator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRollingDmaStateMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DmaCrossingLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Term = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransitionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CalculationVersion = table.Column<int>(type: "int", nullable: false),
                    LoggedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmaCrossingLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RollingDmaStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Term = table.Column<int>(type: "int", nullable: false),
                    RollingSum = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BarCount = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    InsufficientHistory = table.Column<bool>(type: "bit", nullable: false),
                    LastBarDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsAligned = table.Column<bool>(type: "bit", nullable: false),
                    UpdatesSinceAnchor = table.Column<int>(type: "int", nullable: false),
                    CalculationVersion = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RollingDmaStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DmaCrossingLogEntries_Ticker_Term_TransitionDate",
                table: "DmaCrossingLogEntries",
                columns: new[] { "Ticker", "Term", "TransitionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RollingDmaStates_Ticker_Term_CalculationVersion",
                table: "RollingDmaStates",
                columns: new[] { "Ticker", "Term", "CalculationVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DmaCrossingLogEntries");

            migrationBuilder.DropTable(
                name: "RollingDmaStates");
        }
    }
}
