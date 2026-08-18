using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceGateParametersPlaceholder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PlaceholderValue",
                table: "GateParameters",
                newName: "WatchThreshold");

            migrationBuilder.AddColumn<decimal>(
                name: "BuyThreshold",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExitThreshold",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "NoBuySessionCount",
                table: "GateParameters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "RsiCompositeWeight",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RsiFloorQuality",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RsiLowerBound",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RsiPeak",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RsiUpperBound",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RvolCeilingMultiple",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RvolFloorMultiple",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RvolFullWeight",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RvolHalfWeight",
                table: "GateParameters",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyThreshold",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "ExitThreshold",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "NoBuySessionCount",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RsiCompositeWeight",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RsiFloorQuality",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RsiLowerBound",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RsiPeak",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RsiUpperBound",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RvolCeilingMultiple",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RvolFloorMultiple",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RvolFullWeight",
                table: "GateParameters");

            migrationBuilder.DropColumn(
                name: "RvolHalfWeight",
                table: "GateParameters");

            migrationBuilder.RenameColumn(
                name: "WatchThreshold",
                table: "GateParameters",
                newName: "PlaceholderValue");
        }
    }
}
