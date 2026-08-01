using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Both FKs point at the shared base Requests table rather than at
/// ExpenseRequests/CashAdvanceRequests directly -- under TPT a single FK
/// column cannot back two sibling-table constraints (see
/// ExpenseRequestConfiguration and SecurityEventConfiguration for the same
/// pattern). Restrict on delete: a retirement link is a historical fact and
/// must not silently disappear if either side is ever deleted.
/// </summary>
public sealed class AdvanceRetirementLinkConfiguration : IEntityTypeConfiguration<AdvanceRetirementLink>
{
    public void Configure(EntityTypeBuilder<AdvanceRetirementLink> builder)
    {
        builder.ToTable("AdvanceRetirementLinks");

        builder.HasKey(l => l.Id);

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(l => l.ExpenseRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(l => l.CashAdvanceRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(l => l.AmountAppliedNgn).HasColumnType("decimal(18,2)");

        builder.Property(l => l.AppliedAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2")
            .IsRequired();

        // One retirement application per claim: a claim retires an advance
        // once, for its full TotalAmountNgn -- it cannot contribute twice.
        builder.HasIndex(l => l.ExpenseRequestId).IsUnique().HasDatabaseName("UQ_AdvanceRetirementLink_ExpenseRequest");
        builder.HasIndex(l => l.CashAdvanceRequestId).HasDatabaseName("IX_AdvanceRetirementLink_CashAdvanceRequest");
    }
}
