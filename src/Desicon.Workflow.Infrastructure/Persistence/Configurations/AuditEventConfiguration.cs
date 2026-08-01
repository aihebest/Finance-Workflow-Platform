using Desicon.Workflow.Domain.Audit;
using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Append-only, hash-chained audit trail (docs/03-Data-Model-ERD.md section
/// 2.4). The "no UPDATE/DELETE for the application identity" rule is a
/// database permissions concern (GRANT/DENY against a specific login) rather
/// than a table constraint, and no such login is defined anywhere in this
/// scaffold yet -- that is a deployment-time follow-up, not part of this
/// migration. What the schema enforces here is that a Request cannot be
/// deleted out from under its trail (<see cref="DeleteBehavior.Restrict"/>).
/// </summary>
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");

        builder.HasKey(a => a.AuditEventId);
        builder.Property(a => a.AuditEventId).ValueGeneratedOnAdd();

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(a => a.RequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(a => a.EventType).HasMaxLength(50).IsRequired();
        builder.Property(a => a.FromState).HasMaxLength(50);
        builder.Property(a => a.ToState).HasMaxLength(50);
        builder.Property(a => a.ActorId).IsRequired();
        builder.Property(a => a.ActorRole).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(1000);
        builder.Property(a => a.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(a => a.ClientIpAddress).HasMaxLength(45);
        builder.Property(a => a.CorrelationId).HasMaxLength(100);
        builder.Property(a => a.OccurredAtUtc)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2")
            .IsRequired();

        // SHA-256 digests: fixed 32 bytes.
        builder.Property(a => a.PreviousHash).HasColumnType("varbinary(32)");
        builder.Property(a => a.EventHash).HasColumnType("varbinary(32)").IsRequired();

        builder.Property(a => a.IdempotencyKey).HasMaxLength(100);

        // Lets RequestActionService detect a retried action before it
        // re-executes, and lets the database reject a second concurrent
        // execution racing the same key even if the app-level check missed it.
        builder.HasIndex(a => new { a.RequestId, a.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UQ_AuditEvent_Idempotency");
    }
}
