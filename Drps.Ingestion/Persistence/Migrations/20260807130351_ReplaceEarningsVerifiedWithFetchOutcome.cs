using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceEarningsVerifiedWithFetchOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FetchOutcome",
                table: "RawEarningsObservations",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "Unknown");

            // Backfill, not left at the schema default of Unknown for every pre-existing row.
            // A pre-existing row with Verified=1 is direct, known proof a real upcoming date
            // was found - Verified was only ever true when NextEarningsDate was non-null,
            // per the pre-2026-08-07 FinnhubEarningsFeeder invariant - so
            // UpcomingEarningsFound is a known fact, not a guess, for those rows specifically.
            // Verified=0 rows are deliberately left at the Unknown default: the old code
            // never recorded whether a false result meant "genuinely no earnings found" or
            // "couldn't parse the response" - that distinction is genuinely unrecoverable for
            // historical rows, so Unknown (the fail-closed value) is the honest choice here,
            // not a guess in either direction. Same "backfill known history, don't silently
            // default it away" discipline as AddGateScorePerWindowDmaVerification's own
            // precedent.
            migrationBuilder.Sql(
                "UPDATE [RawEarningsObservations] SET [FetchOutcome] = 'UpcomingEarningsFound' WHERE [Verified] = 1;");

            migrationBuilder.DropColumn(
                name: "Verified",
                table: "RawEarningsObservations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FetchOutcome",
                table: "RawEarningsObservations");

            migrationBuilder.AddColumn<bool>(
                name: "Verified",
                table: "RawEarningsObservations",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
