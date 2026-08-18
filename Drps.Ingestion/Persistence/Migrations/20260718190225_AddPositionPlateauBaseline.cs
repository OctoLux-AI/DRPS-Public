using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionPlateauBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PlateauBaselineDate",
                table: "Positions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlateauBaselinePrice",
                table: "Positions",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlateauBaselineDate",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "PlateauBaselinePrice",
                table: "Positions");
        }
    }
}
