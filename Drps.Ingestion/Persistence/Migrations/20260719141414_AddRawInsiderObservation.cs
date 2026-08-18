using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawInsiderObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawInsiderObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DollarValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    InsiderName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawInsiderObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawInsiderObservations_Ticker_Source_TransactionDate",
                table: "RawInsiderObservations",
                columns: new[] { "Ticker", "Source", "TransactionDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawInsiderObservations");
        }
    }
}
