using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniverseSnapshotCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UniverseSnapshotCoverages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourcesSucceeded = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UniverseSnapshotCoverages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UniverseSnapshotCoverages_SnapshotDate",
                table: "UniverseSnapshotCoverages",
                column: "SnapshotDate",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UniverseSnapshotCoverages");
        }
    }
}
