using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BarVerifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Resolution = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SourceCount = table.Column<int>(type: "int", nullable: false),
                    MatchedSourceCount = table.Column<int>(type: "int", nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ToleranceApplied = table.Column<decimal>(type: "decimal(9,6)", nullable: false),
                    PrimarySourceValue = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ComputationVersion = table.Column<int>(type: "int", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarVerifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Discrepancies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FieldOrBarType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceA = table.Column<int>(type: "int", nullable: false),
                    ValueA = table.Column<decimal>(type: "decimal(18,9)", nullable: false),
                    SourceB = table.Column<int>(type: "int", nullable: false),
                    ValueB = table.Column<decimal>(type: "decimal(18,9)", nullable: false),
                    PercentDiff = table.Column<decimal>(type: "decimal(9,6)", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Discrepancies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FieldVerifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceCount = table.Column<int>(type: "int", nullable: false),
                    MatchedSourceCount = table.Column<int>(type: "int", nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ToleranceApplied = table.Column<decimal>(type: "decimal(9,6)", nullable: false),
                    ComputationVersion = table.Column<int>(type: "int", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldVerifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RawFieldObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,9)", nullable: true),
                    RawValueText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IngestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawFieldObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RawOhlcvBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Resolution = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    High = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Low = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    AdjustmentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IngestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawOhlcvBars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceStatuses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<int>(type: "int", nullable: false),
                    FieldOrBarType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TrustState = table.Column<int>(type: "int", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    MatchedObservationCount = table.Column<int>(type: "int", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BarVerifications_Symbol_Timestamp_Resolution",
                table: "BarVerifications",
                columns: new[] { "Symbol", "Timestamp", "Resolution" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldVerifications_Symbol_FieldName_Timestamp",
                table: "FieldVerifications",
                columns: new[] { "Symbol", "FieldName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RawFieldObservations_Symbol_FieldName_Timestamp",
                table: "RawFieldObservations",
                columns: new[] { "Symbol", "FieldName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RawOhlcvBars_Symbol_Timestamp_Resolution",
                table: "RawOhlcvBars",
                columns: new[] { "Symbol", "Timestamp", "Resolution" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceStatuses_Source_FieldOrBarType",
                table: "SourceStatuses",
                columns: new[] { "Source", "FieldOrBarType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BarVerifications");

            migrationBuilder.DropTable(
                name: "Discrepancies");

            migrationBuilder.DropTable(
                name: "FieldVerifications");

            migrationBuilder.DropTable(
                name: "RawFieldObservations");

            migrationBuilder.DropTable(
                name: "RawOhlcvBars");

            migrationBuilder.DropTable(
                name: "SourceStatuses");
        }
    }
}
