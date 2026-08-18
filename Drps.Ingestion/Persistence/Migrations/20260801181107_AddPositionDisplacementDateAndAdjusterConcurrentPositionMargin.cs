using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionDisplacementDateAndAdjusterConcurrentPositionMargin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DisplacementDate",
                table: "Positions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcurrentPositionDisplacementMarginPercent",
                table: "AdjusterParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0.10m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplacementDate",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "ConcurrentPositionDisplacementMarginPercent",
                table: "AdjusterParameters");
        }
    }
}
