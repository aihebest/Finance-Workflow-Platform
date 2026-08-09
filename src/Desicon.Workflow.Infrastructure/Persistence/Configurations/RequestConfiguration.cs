using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Engine-facing root of the table-per-type hierarchy. See
/// docs/03-Data-Model-ERD.md sections 1, 2.1, 2.3 and 4.
/// </summary>
public sealed class RequestConfiguration : IEntityTypeConfiguration<Request>
{
    public void Configure(EntityTypeBuilder<Request> builder)
    {
        builder.ToTable("Requests");
        builder.UseTptMappingStrategy();

        builder.HasKey(r => r.RequestId);
        builder.Property(r => r.RequestId).ValueGeneratedNever();

        builder.Property(r => r.RequestNumber).HasMaxLength(30).IsRequired();
        builder.HasIndex(r => r.RequestNumber).IsUnique().HasDatabaseName("UQ_RequestNumber");

        builder.Property(r => r.ModuleKey).HasMaxLength(50).IsRequired();
        builder.Property(r => r.FormCode).HasMaxLength(20).IsRequired();
        builder.Property(r => r.FormRevision).HasMaxLength(10).IsRequired();
        builder.Property(r => r.TreasuryNumber).HasMaxLength(50);
        builder.Property(r => r.JournalVoucherNumber).HasMaxLength(50);

        // Defaulted at the database rather than only in code, so a row written
        // by anything that bypasses the aggregate -- a data fix, a bulk import
        // -- still carries a version rather than a 0 that resolves to nothing.
        builder.Property(r => r.DefinitionVersion).HasDefaultValue(2);

        // Business Central document numbers are the reference Accounts quote
        // when tracing a payment, so this is indexed: "which request is
        // BC document 12345" is a question that gets asked, and answering it
        // with a table scan on a growing table is the kind of thing nobody
        // notices until the table is large.
        builder.Property(r => r.BcDocumentNumber).HasMaxLength(50);
        builder.HasIndex(r => r.BcDocumentNumber)
            .HasDatabaseName("IX_Request_BcDocumentNumber")
            .HasFilter("[BcDocumentNumber] IS NOT NULL");
        builder.Property(r => r.CurrentState).HasMaxLength(50).IsRequired();

        builder.Property(r => r.StateEnteredAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(r => r.SlaDueAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(r => r.SubmittedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");
        builder.Property(r => r.ClosedAt)
            .HasConversion(DateTimeOffsetConverters.ToNullableUtcDateTime2)
            .HasColumnType("datetime2");

        builder.Property(r => r.TotalAmountNgn).HasColumnType("decimal(18,2)");

        // Optimistic concurrency (docs/03 section 2.3): two actors resolving
        // the same escalated item in the same second must get a conflict, not
        // a silent overwrite.
        builder.Property(r => r.RowVersion).IsRowVersion();

        // Set once by the action pipeline immediately before guard evaluation
        // for the in-flight action; it is not part of the request's persisted
        // state (that trail lives in AuditEvent.ActorId per action).
        builder.Ignore(r => r.ActorId);
        builder.Ignore(r => r.IsClosed);

        // ModuleKey references WorkflowDefinition, which is data (the
        // modules/*.workflow.json files), not a database table -- it stays a
        // plain column. RequesterId, CurrentActorId and DepartmentId now
        // reference real Employee/Department rows.
        builder.Property(r => r.RequesterId).IsRequired();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.RequesterId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable: a request between states genuinely has no current actor
        // (e.g. the moment after submission, before the engine assigns the
        // first approver) or has left the workflow (closed/rejected).
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.CurrentActorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(r => r.DepartmentId).IsRequired();

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(r => r.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // docs/03 section 4 — dashboard indexes.
        builder.HasIndex(r => new { r.CurrentActorId, r.CurrentState })
            .IncludeProperties(r => new { r.RequestNumber, r.SlaDueAt, r.TotalAmountNgn })
            .HasDatabaseName("IX_Request_Actor_State");

        builder.HasIndex(r => r.SlaDueAt)
            .HasFilter("[ClosedAt] IS NULL")
            .HasDatabaseName("IX_Request_Sla_Breach");

        builder.HasIndex(r => new { r.ModuleKey, r.CurrentState, r.SubmittedAt })
            .IncludeProperties(r => new { r.TotalAmountNgn, r.DepartmentId })
            .HasDatabaseName("IX_Request_Ageing");
    }
}
