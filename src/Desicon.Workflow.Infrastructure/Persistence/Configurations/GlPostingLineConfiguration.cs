using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// A line of the journal voucher assembled in the "FOR ACF DEPT USE ONLY"
/// block. Shared by every module -- keyed to the base <see cref="Request"/>
/// row, not to a specific module table (see docs/03-Data-Model-ERD.md
/// section 1: "REQUEST ||--o{ GL_POSTING_LINE : posts").
///
/// CK_GlBalanced ("SUM(debits) = SUM(credits)" per request, docs/03 section
/// 3) is deliberately not a CHECK constraint here: it is an aggregate across
/// sibling rows, which a per-row CHECK cannot express. The doc itself notes
/// it is "enforced in the posting transaction" -- application code, inside
/// the same transaction that inserts these rows.
/// </summary>
public sealed class GlPostingLineConfiguration : IEntityTypeConfiguration<GlPostingLine>
{
    public void Configure(EntityTypeBuilder<GlPostingLine> builder)
    {
        builder.ToTable("GlPostingLines");

        builder.HasKey(l => l.PostingLineId);
        builder.Property(l => l.PostingLineId).ValueGeneratedNever();

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(l => l.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(l => l.Side)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(l => l.AccountNumber).HasMaxLength(20).IsRequired();
        builder.Property(l => l.Narration).HasMaxLength(500);
        builder.Property(l => l.AmountNgn).HasColumnType("decimal(18,2)");
    }
}
