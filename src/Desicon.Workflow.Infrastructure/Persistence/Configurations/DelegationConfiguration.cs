using Desicon.Workflow.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

public sealed class DelegationConfiguration : IEntityTypeConfiguration<Delegation>
{
    public void Configure(EntityTypeBuilder<Delegation> builder)
    {
        builder.ToTable("Delegations", t => t.HasCheckConstraint(
            "CK_Delegation_Window",
            "([EndsAt] >= [StartsAt])"));

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(d => d.FromEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(d => d.ToEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(d => d.StartsAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2")
            .IsRequired();
        builder.Property(d => d.EndsAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(d => d.Reason).HasMaxLength(500);

        // EmployeeActorResolver's per-request cache still queries this on
        // every uncached resolution, filtered to the actors already in a
        // base-resolved set -- FromEmployeeId plus the active window is the
        // lookup path, IsActive first since it is the cheapest predicate.
        builder.HasIndex(d => new { d.IsActive, d.FromEmployeeId, d.StartsAt, d.EndsAt })
            .HasDatabaseName("IX_Delegation_Active_From");
    }
}
