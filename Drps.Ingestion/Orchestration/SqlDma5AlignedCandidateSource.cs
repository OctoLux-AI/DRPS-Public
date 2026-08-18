using Drps.Shared.Models;
using Microsoft.Data.SqlClient;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Real <see cref="IDma5AlignedCandidateSource"/> implementation - raw, parameterized
/// Microsoft.Data.SqlClient against the existing RollingDmaStates table (owned and written by
/// Drps.Calculator's RollingDmaStateService), never EF Core or any Drps.Shared DbContext - same
/// isolation pattern Drps.Diagnostics/TickerBotVerificationProbe.cs already established for
/// reading this same "Drps" LocalDB without a ProjectReference to the project that owns the
/// table being read.
///
/// Drps.Ingestion carries no ProjectReference to Drps.Calculator (a deliberate project-boundary
/// rule for this class - CLAUDE.md's 2026-08-08 decision), so
/// RollingDmaStateService.CalculationVersion cannot be referenced directly. Its current value
/// (1) is duplicated by hand below - the same "duplicated standalone, update by hand if the
/// source ever changes" precedent TickerBotVerificationProbe's own doc comment already
/// establishes for its hand-copied BarReconciliationService tolerance constants.
/// DmaAlignmentTerm.Term5 IS referenced directly below (Drps.Shared, not Drps.Calculator - no
/// boundary violation) rather than a second hand-copied literal.
///
/// Read-only against RollingDmaStates - this class never writes to it. Column names/types
/// (Ticker nvarchar(16), Term int, IsAligned bit, CalculationVersion int) confirmed directly
/// against the real schema (RollingDmaStateConfiguration.cs and the AddRollingDmaStateMachine
/// migration) before this query was written, not assumed.
/// </summary>
public sealed class SqlDma5AlignedCandidateSource : IDma5AlignedCandidateSource
{
    // Matches Drps.Calculator/Dma/RollingDma/RollingDmaStateService.CalculationVersion's current
    // value - see this class's own doc comment for why it's duplicated rather than referenced.
    private const int CalculationVersion = 1;

    private readonly string _connectionString;

    public SqlDma5AlignedCandidateSource(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<string>> GetDma5AlignedTickersAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Ticker
            FROM dbo.RollingDmaStates
            WHERE Term = @Term
                AND IsAligned = 1
                AND CalculationVersion = @CalculationVersion
            ORDER BY Ticker
            """;

        var tickers = new List<string>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Term", (int)DmaAlignmentTerm.Term5);
        command.Parameters.AddWithValue("@CalculationVersion", CalculationVersion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }
}
