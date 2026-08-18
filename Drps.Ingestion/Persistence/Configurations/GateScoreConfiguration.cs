using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class GateScoreConfiguration : IEntityTypeConfiguration<GateScore>
{
    public void Configure(EntityTypeBuilder<GateScore> builder)
    {
        builder.Property(s => s.RsiValue).HasColumnType("decimal(18,4)");
        builder.Property(s => s.RsiQuality).HasColumnType("decimal(18,4)");
        builder.Property(s => s.RvolValue).HasColumnType("decimal(18,4)");
        builder.Property(s => s.RvolQuality).HasColumnType("decimal(18,4)");
        builder.Property(s => s.AtrValue).HasColumnType("decimal(18,4)");
        builder.Property(s => s.AtrCleanPreEventValue).HasColumnType("decimal(18,4)");
        builder.Property(s => s.CompositeScore).HasColumnType("decimal(18,4)");

        // Genuinely optional, not a default-valued column - a null Sector means "cannot
        // verify sector," which Adjuster's future cap logic must be able to distinguish from
        // any real sector name. No HasDefaultValue here, deliberately.
        builder.Property(s => s.Sector).IsRequired(false);

        // Genuinely optional, same reasoning as Sector above - null means "not a
        // rejection," non-null names the GateRejectionReason that produced this row
        // (CLAUDE.md's "Gate: Rejection Reasons Now Persisted," 2026-08-06). No
        // HasDefaultValue, additive-only, no backfill needed on pre-existing rows (every
        // row written before this decision is correctly NULL - none of them were ever a
        // rejection under the old discard-everything behavior).
        builder.Property(s => s.RejectionReason).IsRequired(false);

        // String-backed, not the numeric default - a raw int would silently renumber if
        // GateBucket's members were ever reordered, same risk class as the SourceType enum
        // pinning fix elsewhere in this codebase.
        builder.Property(s => s.Bucket).HasConversion<string>();

        // Blast-radius scoping (CLAUDE.md 2026-08-04) - explicit defaults, same reasoning as
        // every other bool column below: EF's auto-generated migration default for a new
        // NOT NULL bool column has repeatedly needed hand-correction in this codebase without
        // one (see CLAUDE.md's own precedent on AllSourcesSucceeded/OpenOrigin/etc.).
        builder.Property(s => s.IsDma5VerifiedAsync_Result).HasDefaultValue(false);
        builder.Property(s => s.IsDma15VerifiedAsync_Result).HasDefaultValue(false);
        builder.Property(s => s.IsDma30VerifiedAsync_Result).HasDefaultValue(false);
        builder.Property(s => s.IsDma60VerifiedAsync_Result).HasDefaultValue(false);

        builder.Property(s => s.HasTiingoCorrectedData).HasDefaultValue(false);
        builder.Property(s => s.HasUnverifiedPartialCreditData).HasDefaultValue(false);
        builder.Property(s => s.IsStaleData).HasDefaultValue(false);
        builder.Property(s => s.IsEarningsBlackoutActive).HasDefaultValue(false);
        builder.Property(s => s.EarningsDataUnverified).HasDefaultValue(false);

        // FK reference to GateParameters.Id, no navigation property either side - a
        // GateScore row only needs to know which parameter version scored it, not to
        // navigate back to it in code. Restrict (not the EF default Cascade): these are
        // append-only tables, nothing should ever delete a GateParameters row out from
        // under an existing GateScore, and if one somehow were, it must not silently take
        // GateScore rows with it.
        builder.HasOne<GateParameters>()
            .WithMany()
            .HasForeignKey(s => s.GateParameterVersion)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
