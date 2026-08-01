using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// DEL-AC-FRM-002. Table-per-type child of <see cref="Request"/>. See
/// docs/03-Data-Model-ERD.md section 1 and the constraints table in section 3.
/// </summary>
public sealed class ExpenseRequestConfiguration : IEntityTypeConfiguration<ExpenseRequest>
{
    public void Configure(EntityTypeBuilder<ExpenseRequest> builder)
    {
        builder.ToTable("ExpenseRequests", t => t.HasCheckConstraint(
            "CK_MakerChecker_Expense",
            "([PostedByUserId] IS NULL OR [AuthorisedByUserId] IS NULL OR " +
            "[PostedByUserId] <> [AuthorisedByUserId])"));

        builder.Property(e => e.BeneficiaryId).IsRequired();

        builder.HasOne<Beneficiary>()
            .WithMany()
            .HasForeignKey(e => e.BeneficiaryId)
            .OnDelete(DeleteBehavior.Restrict);

        // "EXPENSE_REQUEST }o--o| CASH_ADVANCE_REQUEST : retires" -- a claim
        // may retire an advance, but a deleted advance must not silently take
        // its retiring claims with it.
        builder.HasOne<CashAdvanceRequest>()
            .WithMany()
            .HasForeignKey(e => e.RetiresAdvanceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(e => e.AdvanceAmountNgn).HasColumnType("decimal(18,2)");
        builder.Property(e => e.RefundReceivedAmountNgn).HasColumnType("decimal(18,2)");
        builder.Ignore(e => e.NetPayableNgn);
        builder.Ignore(e => e.IsRefundDue);
        builder.Ignore(e => e.GlDebitTotal);
        builder.Ignore(e => e.GlCreditTotal);

        builder.Property(e => e.ReceiptStatus)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.PaymentMethod)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.AmountInWords).HasMaxLength(200);
        builder.Property(e => e.PaymentReference).HasMaxLength(50);

        builder.Property(e => e.PostedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(e => e.PaymentDate)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(e => e.AcknowledgedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");

        builder.HasMany(e => e.Lines)
            .WithOne()
            .HasForeignKey(l => l.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // GlPostingLine keys to the shared base Request row (see
        // GlPostingLineConfiguration) rather than to ExpenseRequest
        // specifically -- under TPT a single FK column cannot enforce two
        // sibling-table constraints at once. Load explicitly by RequestId.
        builder.Ignore(e => e.GlPostingLines);
    }
}
