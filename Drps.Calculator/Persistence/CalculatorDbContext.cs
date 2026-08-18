using Drps.Calculator.Persistence.Configurations;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Persistence;

public class CalculatorDbContext : DbContext
{
    public CalculatorDbContext(DbContextOptions<CalculatorDbContext> options) : base(options)
    {
    }

    // Read-only from Calculator's side - owned/migrated by Drps.Ingestion.
    public DbSet<RawOhlcvBar> RawOhlcvBars => Set<RawOhlcvBar>();

    public DbSet<BarVerification> BarVerifications => Set<BarVerification>();

    public DbSet<RawExDividendObservation> RawExDividendObservations => Set<RawExDividendObservation>();

    // Read-only from Calculator's side, same as BarVerification/RawOhlcvBar above - owned and
    // migrated by Drps.Ingestion. Added so the four ComputationServices can look up which
    // dates were resolved via the OHL-agreed/Close-resolved-to-Tiingo exception (CLAUDE.md,
    // 2026-07-17) without duplicating that logic outside BarReconciliationService.
    public DbSet<Discrepancy> Discrepancies => Set<Discrepancy>();

    // Owned by Drps.Calculator.
    public DbSet<DmaIndicator> DmaIndicators => Set<DmaIndicator>();

    public DbSet<RsiIndicator> RsiIndicators => Set<RsiIndicator>();

    public DbSet<RvolIndicator> RvolIndicators => Set<RvolIndicator>();

    public DbSet<AtrIndicator> AtrIndicators => Set<AtrIndicator>();

    // Watch-only per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked"
    // (2026-07-31) - computed and persisted, not yet wired into any Adjuster/Execution
    // consumer. Owned by Drps.Calculator, same as every other indicator above.
    public DbSet<RsiSlopeIndicator> RsiSlopeIndicators => Set<RsiSlopeIndicator>();

    public DbSet<RsiConcavityIndicator> RsiConcavityIndicators => Set<RsiConcavityIndicator>();

    // Owned by Drps.Calculator - the Rolling DMA State Machine's mutable state tracker and its
    // append-only crossing log (CLAUDE.md, "Rolling DMA State Machine", 2026-07-22).
    public DbSet<RollingDmaState> RollingDmaStates => Set<RollingDmaState>();

    public DbSet<DmaCrossingLogEntry> DmaCrossingLogEntries => Set<DmaCrossingLogEntry>();

    // Read-only from Calculator's side - owned/migrated by Drps.Ingestion. Needed so
    // RollingDmaStateService can read the full nightly universe list without any pool
    // (DMA pre-screen included) ever gating which tickers get ingested/screened, per
    // CLAUDE.md's "Ingestion Eligibility: No Pool Bifurcation" decision.
    public DbSet<UniverseSnapshot> UniverseSnapshots => Set<UniverseSnapshot>();

    // Read-only from Calculator's side, same as UniverseSnapshot above - owned/migrated by
    // Drps.Ingestion. Lets RollingDmaStateService log a clear warning when a night's universe
    // is known-partial, without changing its existing fail-open-to-what-data-exists behavior.
    public DbSet<UniverseSnapshotCoverage> UniverseSnapshotCoverages => Set<UniverseSnapshotCoverage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DmaIndicatorConfiguration).Assembly);

        modelBuilder.Entity<RawOhlcvBar>()
            .HasIndex(b => new { b.Symbol, b.Timestamp, b.Resolution });

        modelBuilder.Entity<BarVerification>()
            .HasIndex(b => new { b.Symbol, b.Timestamp, b.Resolution });

        modelBuilder.Entity<RawExDividendObservation>()
            .HasIndex(o => new { o.Symbol, o.ExDividendDate });
    }
}
