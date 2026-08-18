using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawExDividendObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawExDividendObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExDividendDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    VariancePct = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IngestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawExDividendObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawExDividendObservations_Symbol_ExDividendDate_Source",
                table: "RawExDividendObservations",
                columns: new[] { "Symbol", "ExDividendDate", "Source" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawExDividendObservations");
        }
    }
}
