using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawSectorObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawSectorObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SectorValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SicCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawSectorObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawSectorObservations_Ticker_Source_FetchedAt",
                table: "RawSectorObservations",
                columns: new[] { "Ticker", "Source", "FetchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawSectorObservations");
        }
    }
}
