using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerRunRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerRunRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RunDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerRunRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerRunRecords_WorkerName_RunDate",
                table: "WorkerRunRecords",
                columns: new[] { "WorkerName", "RunDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerRunRecords");
        }
    }
}
