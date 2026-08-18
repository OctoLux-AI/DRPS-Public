using Drps.Ingestion.Persistence.Configurations;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Persistence;

public class DrpsDbContext : DbContext
{
    public DrpsDbContext(DbContextOptions<DrpsDbContext> options) : base(options)
    {
    }

    public DbSet<RawOhlcvBar> RawOhlcvBars => Set<RawOhlcvBar>();

    public DbSet<RawFieldObservation> RawFieldObservations => Set<RawFieldObservation>();

    public DbSet<RawExDividendObservation> RawExDividendObservations => Set<RawExDividendObservation>();

    public DbSet<BarVerification> BarVerifications => Set<BarVerification>();

    public DbSet<FieldVerification> FieldVerifications => Set<FieldVerification>();

    public DbSet<Discrepancy> Discrepancies => Set<Discrepancy>();

    public DbSet<SourceStatus> SourceStatuses => Set<SourceStatus>();

    public DbSet<GateParameters> GateParameters => Set<GateParameters>();

    public DbSet<GateScore> GateScores => Set<GateScore>();

    public DbSet<RawSectorObservation> RawSectorObservations => Set<RawSectorObservation>();

    public DbSet<RawEarningsObservation> RawEarningsObservations => Set<RawEarningsObservation>();

    public DbSet<RawInsiderObservation> RawInsiderObservations => Set<RawInsiderObservation>();

    public DbSet<AdjusterAllocation> AdjusterAllocations => Set<AdjusterAllocation>();

    public DbSet<AdjusterParameters> AdjusterParameters => Set<AdjusterParameters>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<WorkerRunRecord> WorkerRunRecords => Set<WorkerRunRecord>();

    public DbSet<KillSwitchCounter> KillSwitchCounters => Set<KillSwitchCounter>();

    public DbSet<ConsecutiveLossCircuitBreaker> ConsecutiveLossCircuitBreakers => Set<ConsecutiveLossCircuitBreaker>();

    public DbSet<PositionReconciliationDiscrepancy> PositionReconciliationDiscrepancies => Set<PositionReconciliationDiscrepancy>();

    public DbSet<ExcludedTicker> ExcludedTickers => Set<ExcludedTicker>();

    public DbSet<UniverseSnapshot> UniverseSnapshots => Set<UniverseSnapshot>();

    // One row per SnapshotDate - visibility into whether that day's UniverseSnapshot reflects
    // the full universe or a partial one (CLAUDE.md's fail-closed-everywhere principle,
    // 2026-07-22). See UniverseSnapshotCoverage's own doc comment.
    public DbSet<UniverseSnapshotCoverage> UniverseSnapshotCoverages => Set<UniverseSnapshotCoverage>();

    public DbSet<WeeklyVarianceAuditEntry> WeeklyVarianceAuditEntries => Set<WeeklyVarianceAuditEntry>();

    public DbSet<RawRegimeObservation> RawRegimeObservations => Set<RawRegimeObservation>();

    // CLAUDE.md's 2026-07-31 retry-ambiguity audit, Gap 1 - one-time, self-clearing skip for a
    // ticker's next OPEN fire attempt after an AmbiguousUnresolved outcome. See
    // AmbiguousFireSkip's own doc comment for why this is deliberately not ExcludedTicker.
    public DbSet<AmbiguousFireSkip> AmbiguousFireSkips => Set<AmbiguousFireSkip>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BarVerificationConfiguration).Assembly);

        modelBuilder.Entity<RawOhlcvBar>()
            .HasIndex(b => new { b.Symbol, b.Timestamp, b.Resolution });

        modelBuilder.Entity<BarVerification>()
            .HasIndex(b => new { b.Symbol, b.Timestamp, b.Resolution });

        modelBuilder.Entity<RawFieldObservation>()
            .HasIndex(f => new { f.Symbol, f.FieldName, f.Timestamp });

        modelBuilder.Entity<FieldVerification>()
            .HasIndex(f => new { f.Symbol, f.FieldName, f.Timestamp });

        modelBuilder.Entity<SourceStatus>()
            .HasIndex(s => new { s.Source, s.FieldOrBarType });

        modelBuilder.Entity<RawExDividendObservation>()
            .HasIndex(o => new { o.Symbol, o.ExDividendDate, o.Source });

        modelBuilder.Entity<RawSectorObservation>()
            .HasIndex(o => new { o.Ticker, o.Source, o.FetchedAt });

        modelBuilder.Entity<RawEarningsObservation>()
            .HasIndex(o => new { o.Ticker, o.Source, o.FetchedAt });

        modelBuilder.Entity<RawInsiderObservation>()
            .HasIndex(o => new { o.Ticker, o.Source, o.TransactionDate });

        modelBuilder.Entity<KillSwitchCounter>()
            .HasIndex(k => k.TradingDate);

        modelBuilder.Entity<RawRegimeObservation>()
            .HasIndex(o => new { o.Ticker, o.Source, o.ObservationDate });
    }
}
