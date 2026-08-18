using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerRunRecordPartialSuccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllSourcesSucceeded",
                table: "WorkerRunRecords",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureDetail",
                table: "WorkerRunRecords",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllSourcesSucceeded",
                table: "WorkerRunRecords");

            migrationBuilder.DropColumn(
                name: "FailureDetail",
                table: "WorkerRunRecords");
        }
    }
}
