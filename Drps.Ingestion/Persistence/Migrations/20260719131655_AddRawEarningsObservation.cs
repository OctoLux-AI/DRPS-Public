using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawEarningsObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawEarningsObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    NextEarningsDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawEarningsObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawEarningsObservations_Ticker_Source_FetchedAt",
                table: "RawEarningsObservations",
                columns: new[] { "Ticker", "Source", "FetchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawEarningsObservations");
        }
    }
}
