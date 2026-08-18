using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKillSwitchCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KillSwitchCounters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TradingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenAttemptCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KillSwitchCounters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KillSwitchCounters_TradingDate",
                table: "KillSwitchCounters",
                column: "TradingDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KillSwitchCounters");
        }
    }
}
