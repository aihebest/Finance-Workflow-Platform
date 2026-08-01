using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// One line of a cash advance request. DEL-AC-FRM-003 does not itemise
/// allocation per line -- that lives on the header (see
/// <see cref="CashAdvanceRequestConfiguration"/>, CK_Advance_Allocation).
/// </summary>
public sealed class AdvanceLineConfiguration : IEntityTypeConfiguration<AdvanceLine>
{
    public void Configure(EntityTypeBuilder<AdvanceLine> builder)
    {
        builder.ToTable("AdvanceLines", t => t.HasCheckConstraint(
            "CK_Currency_AdvanceLine", "([CurrencyCode] <> 'NGN' OR [FxRate] = 1.0)"));

        builder.HasKey(l => l.LineId);
        builder.Property(l => l.LineId).ValueGeneratedNever();

        builder.Property(l => l.RequestId).IsRequired();
        builder.Property(l => l.LineNumber).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(500).IsRequired();

        builder.Property(l => l.FxRateDate).HasColumnType("date");

        builder.Property(l => l.CurrencyCode)
            .HasColumnType("char(3)")
            .IsFixedLength()
            .IsRequired();

        builder.Property(l => l.Amount).HasColumnType("decimal(18,2)");

        // See ExpenseLineConfiguration -- a rate, not an "amount".
        builder.Property(l => l.FxRate).HasColumnType("decimal(18,6)");

        builder.Property(l => l.AmountNgn).HasColumnType("decimal(18,2)");
    }
}
