using Desicon.Workflow.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

/// <summary>
/// No foreign key to Requests, deliberately.
///
/// This file used to say exactly that while declaring one. The constraint
/// defeated the very design it claimed to protect: ISecurityEventWriter opens
/// its own connection precisely so a denial survives the business transaction
/// rolling back — and a foreign key makes that write impossible, because the
/// request row it references never commits and a separate connection cannot
/// see an uncommitted one. The mechanism guaranteeing the record survives was
/// guaranteed to fail in the one case it existed for.
///
/// It also blocked a legitimate write in the other direction: creating a
/// beneficiary while raising a claim, where the request exists in memory but
/// has not been saved.
///
/// So RequestId is now a plain, indexed column. Dashboards still join on it;
/// nothing can refuse a security write because of it. That is a deliberate
/// trade of referential validation for the guarantee that a record of a
/// refusal is always written — and for this column, the second matters more.
/// A SecurityEvent naming a request that no longer exists is still evidence;
/// a SecurityEvent that was never written is not.
/// </summary>
public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents");

        builder.HasKey(s => s.SecurityEventId);
        builder.Property(s => s.SecurityEventId).ValueGeneratedOnAdd();

        // RequestId is stored and indexed but is not a relationship. See the
        // class remarks: a constraint here can refuse a security write, and
        // nothing may do that.
        builder.Property(s => s.RequestId).IsRequired();

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
