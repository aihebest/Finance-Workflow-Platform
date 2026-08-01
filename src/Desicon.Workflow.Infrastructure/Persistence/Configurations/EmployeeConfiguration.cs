using Desicon.Workflow.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.EntraObjectId).HasMaxLength(100).IsRequired();
        builder.HasIndex(e => e.EntraObjectId).IsUnique().HasDatabaseName("UQ_Employee_EntraObjectId");

        builder.Property(e => e.StaffNumber).HasMaxLength(30).IsRequired();
        builder.HasIndex(e => e.StaffNumber).IsUnique().HasDatabaseName("UQ_Employee_StaffNumber");

        builder.Property(e => e.FullName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Email).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Grade).HasMaxLength(50);
        builder.Property(e => e.DefaultCostCentreCode).HasMaxLength(30);
        builder.Property(e => e.BankName).HasMaxLength(100);
        builder.Property(e => e.BankAccountNumber).HasMaxLength(30);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(e => e.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Self-referencing line-manager chain. Restrict, not Cascade or
        // SetNull -- deleting a manager out from under their reports must be
        // an explicit reassignment, never an implicit side effect.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(e => e.LineManagerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.LineManagerId).HasDatabaseName("IX_Employee_LineManager");
        builder.HasIndex(e => new { e.DepartmentId, e.IsActive }).HasDatabaseName("IX_Employee_Department_Active");
    }
}
