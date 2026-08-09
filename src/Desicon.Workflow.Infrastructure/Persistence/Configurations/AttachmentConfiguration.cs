using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Receipt and supporting-document metadata. The bytes live in the immutable
/// blob container; this is the index into it.
/// </summary>
public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments");

        builder.HasKey(a => a.AttachmentId);
        builder.Property(a => a.AttachmentId).ValueGeneratedNever();

        // Filenames are long, and truncating one silently would make a
        // receipt harder to recognise than it needs to be.
        builder.Property(a => a.FileName).HasMaxLength(260).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.BlobPath).HasMaxLength(200).IsRequired();

        // 64 hex characters. Fixed width because it always is.
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();

        builder.Property(a => a.UploadedAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2");

        // Cascade from the request: an attachment has no meaning without one.
        // Note this deletes the row, not the blob -- the container's WORM
        // policy makes the file itself undeletable, which is the intent. A
        // receipt that vanishes when a record is tidied up is not evidence.
        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(a => a.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every read is "the attachments for this request", and the guard
        // counts them on every transition out of Cost Control.
        builder.HasIndex(a => a.RequestId).HasDatabaseName("IX_Attachment_Request");
    }
}
