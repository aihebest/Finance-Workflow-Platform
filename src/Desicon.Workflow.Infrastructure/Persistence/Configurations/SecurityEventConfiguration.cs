using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// No FK to Requests carrying cascade/restrict semantics that could ever
/// block a security write -- see ISecurityEventWriter. RequestId is still
/// declared as a relationship so EF can validate it at the type level and
/// dashboards can join it, but delete behaviour is Restrict rather than
/// Cascade: a request row must not be deletable out from under a record of
/// someone having been denied access to it.
/// </summary>
public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents");

        builder.HasKey(s => s.SecurityEventId);
        builder.Property(s => s.SecurityEventId).ValueGeneratedOnAdd();

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(s => s.RequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(s => s.ModuleKey).HasMaxLength(50).IsRequired();
        builder.Property(s => s.Action).HasMaxLength(50).IsRequired();
        builder.Property(s => s.FromState).HasMaxLength(50);
        builder.Property(s => s.Reason).HasMaxLength(50).IsRequired();
        builder.Property(s => s.Detail).HasMaxLength(1000);

        builder.Property(s => s.OccurredAtUtc)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.HasIndex(s => new { s.RequestId, s.OccurredAtUtc }).HasDatabaseName("IX_SecurityEvent_Request");
        builder.HasIndex(s => new { s.AttemptedByUserId, s.OccurredAtUtc }).HasDatabaseName("IX_SecurityEvent_Actor");
    }
}
