using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawRegimeObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawRegimeObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    ObservationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    High = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Low = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Close = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawRegimeObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawRegimeObservations_Ticker_Source_ObservationDate",
                table: "RawRegimeObservations",
                columns: new[] { "Ticker", "Source", "ObservationDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawRegimeObservations");
        }
    }
}
