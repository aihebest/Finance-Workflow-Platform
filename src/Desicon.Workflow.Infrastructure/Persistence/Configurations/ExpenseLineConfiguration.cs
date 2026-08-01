using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// One row of the "Details of Expense" table on DEL-AC-FRM-002. See
/// docs/03-Data-Model-ERD.md section 1 and the constraints table in section 3.
/// </summary>
public sealed class ExpenseLineConfiguration : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> builder)
    {
        builder.ToTable("ExpenseLines", t =>
        {
            t.HasCheckConstraint(
                "CK_ExpenseLine_Allocation",
                "(([ProjectCode] IS NOT NULL AND [CostCentreCode] IS NULL) OR " +
                "([ProjectCode] IS NULL AND [CostCentreCode] IS NOT NULL))");

            t.HasCheckConstraint("CK_Currency_ExpenseLine", "([CurrencyCode] <> 'NGN' OR [FxRate] = 1.0)");
        });

        builder.HasKey(l => l.LineId);
        builder.Property(l => l.LineId).ValueGeneratedNever();

        builder.Property(l => l.RequestId).IsRequired();
        builder.Property(l => l.LineNumber).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(500).IsRequired();

        builder.Property(l => l.ExpenseDate).HasColumnType("date");
        builder.Property(l => l.FxRateDate).HasColumnType("date");

        // ExpenseCategoryId / ProjectCode / CostCentreCode / CurrencyCode
        // reference ExpenseCategory, Project, CostCentre and Currency, none
        // of which exist as entities in this scaffold yet, so they are plain
        // columns rather than FK relationships for now.
        builder.Property(l => l.ProjectCode).HasMaxLength(30);
        builder.Property(l => l.CostCentreCode).HasMaxLength(30);

        builder.Property(l => l.CurrencyCode)
            .HasColumnType("char(3)")
            .IsFixedLength()
            .IsRequired();

        builder.Property(l => l.Amount).HasColumnType("decimal(18,2)");

        // A rate, not a currency amount, so it sits outside the docs/03
        // section 2.2 decimal(18,2) rule for "amounts" -- narrower precision
        // here would round NGN/USD-scale rates.
        builder.Property(l => l.FxRate).HasColumnType("decimal(18,6)");

        builder.Property(l => l.AmountNgn).HasColumnType("decimal(18,2)");

        builder.Ignore(l => l.HasValidAllocation);
    }
}
