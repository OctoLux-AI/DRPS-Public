using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drps.Ingestion.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGateScorePerWindowDmaVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDma15VerifiedAsync_Result",
                table: "GateScores",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDma30VerifiedAsync_Result",
                table: "GateScores",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDma5VerifiedAsync_Result",
                table: "GateScores",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDma60VerifiedAsync_Result",
                table: "GateScores",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Backfill, not left at the schema default of false. Every pre-existing GateScore
            // row was written under the old Tier 1 gate, which required ALL FOUR windows to
            // independently verify before a row could ever be persisted at all - so
            // IsDmaVerifiedAsync_Result == true on any existing row is direct proof all four
            // windows were genuinely verified for it, not an unknown to default to false.
            // Leaving the new columns at their false schema default here would falsify every
            // pre-existing row's real, known history - same class of mistake this codebase's
            // own migration precedents (e.g. AddPositionActionOrigin's Unknown-not-guessed
            // backfill) exist to avoid, just in the opposite direction: there the honest value
            // was "unknown," here the honest value is a known "true."
            migrationBuilder.Sql(
                "UPDATE [GateScores] SET [IsDma5VerifiedAsync_Result] = 1, [IsDma15VerifiedAsync_Result] = 1, " +
                "[IsDma30VerifiedAsync_Result] = 1, [IsDma60VerifiedAsync_Result] = 1 " +
                "WHERE [IsDmaVerifiedAsync_Result] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDma15VerifiedAsync_Result",
                table: "GateScores");

            migrationBuilder.DropColumn(
                name: "IsDma30VerifiedAsync_Result",
                table: "GateScores");

            migrationBuilder.DropColumn(
                name: "IsDma5VerifiedAsync_Result",
                table: "GateScores");

            migrationBuilder.DropColumn(
                name: "IsDma60VerifiedAsync_Result",
                table: "GateScores");
        }
    }
}
