using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// DEL-AC-FRM-003. Table-per-type child of <see cref="Request"/>. See
/// docs/03-Data-Model-ERD.md section 1 and the constraints table in section 3.
/// </summary>
public sealed class CashAdvanceRequestConfiguration : IEntityTypeConfiguration<CashAdvanceRequest>
{
    public void Configure(EntityTypeBuilder<CashAdvanceRequest> builder)
    {
        builder.ToTable("CashAdvanceRequests", t =>
        {
            t.HasCheckConstraint(
                "CK_Advance_Allocation",
                "(([AllocationType] = 'Project' AND [ProjectCode] IS NOT NULL) OR " +
                "([AllocationType] = 'CostCentre' AND [CostCentreCode] IS NOT NULL))");

            t.HasCheckConstraint(
                "CK_MakerChecker_Advance",
                "([PostedByUserId] IS NULL OR [AuthorisedByUserId] IS NULL OR " +
                "[PostedByUserId] <> [AuthorisedByUserId])");

            // docs/03 section 3 names this CK_RetirementDue as
            // "RetirementDueDate = AcknowledgedAt + (InStation ? 24h : 72h)".
            // That formula describes an earlier design. The clock now starts
            // at cash release, over WORKING hours from a calendar (see
            // CashAdvanceRequest.ReleaseCash and RetirementClockTests, "per
            // Finance direction of 31 July 2026") -- arithmetic a CHECK
            // constraint cannot reproduce, since it depends on the working
            // calendar, not a fixed offset. What the database can and does
            // enforce is the invariant that actually matters at this layer:
            // a due date is never earlier than the release it was computed
            // from, and never exists without one.
            t.HasCheckConstraint(
                "CK_RetirementDue",
                "([RetirementDueDate] IS NULL OR ([CashReleasedAt] IS NOT NULL AND " +
                "[RetirementDueDate] >= [CashReleasedAt]))");
        });

        builder.Property(a => a.Purpose).HasMaxLength(500);

        builder.Property(a => a.AllocationType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(a => a.ProjectCode).HasMaxLength(30);
        builder.Property(a => a.CostCentreCode).HasMaxLength(30);

        builder.Property(a => a.StationScope)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(a => a.CashReleasedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(a => a.AcknowledgedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(a => a.RetirementDueDate)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");

        builder.Property(a => a.RetirementStatus)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(a => a.RetiredAmountNgn).HasColumnType("decimal(18,2)");

        // Derived from TotalAmountNgn (on the base Requests row) and
        // RetiredAmountNgn (on this row); a SQL Server computed column cannot
        // span two tables, so this stays an in-memory property.
        builder.Ignore(a => a.RetirementBalanceNgn);
        builder.Ignore(a => a.GlDebitTotal);
        builder.Ignore(a => a.GlCreditTotal);

        builder.HasMany(a => a.Lines)
            .WithOne()
            .HasForeignKey(l => l.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // See ExpenseRequestConfiguration: GlPostingLine keys to the shared
        // base Request row, not to this table.
        builder.Ignore(a => a.GlPostingLines);

        // docs/03 section 4 — overdue-advances dashboard index. The doc's
        // INCLUDE list names RetirementBalanceNgn, which (as above) is not a
        // stored column; RetiredAmountNgn is the persisted value the balance
        // is derived from, and covers the same dashboard query.
        builder.HasIndex(a => new { a.RetirementStatus, a.RetirementDueDate })
            .IncludeProperties(a => a.RetiredAmountNgn)
            .HasDatabaseName("IX_Advance_Overdue");
    }
}
