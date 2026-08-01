using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Transactional outbox (docs/03-Data-Model-ERD.md's audit/notification
/// concerns, extended). Written by RequestActionService in the same
/// transaction as the state change it describes; read by OutboxDispatcher out
/// of band.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(m => m.OutboxMessageId);
        builder.Property(m => m.OutboxMessageId).ValueGeneratedOnAdd();

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(m => m.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(m => m.Template).HasMaxLength(100).IsRequired();
        builder.Property(m => m.RecipientRolesJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(m => m.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();

        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(m => m.CreatedAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(m => m.DispatchedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");

        builder.Property(m => m.LastError).HasMaxLength(2000);

        // The dispatcher's poll query: oldest pending row first.
        builder.HasIndex(m => new { m.Status, m.CreatedAt })
            .HasDatabaseName("IX_Outbox_Pending");
    }
}
