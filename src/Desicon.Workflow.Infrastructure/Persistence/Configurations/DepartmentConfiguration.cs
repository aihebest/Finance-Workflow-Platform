using Desicon.Workflow.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

public sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedOnAdd();

        builder.Property(d => d.Code).HasMaxLength(20).IsRequired();
        builder.HasIndex(d => d.Code).IsUnique().HasDatabaseName("UQ_Department_Code");

        builder.Property(d => d.Name).HasMaxLength(200).IsRequired();

        // DepartmentHeadId points at Employee, which itself FKs back to
        // Department -- a genuine cycle. No FK constraint is declared here;
        // Employee.DepartmentId is the FK that is enforced, and this side is
        // left as a plain column to avoid a multiple-cascade-paths error and
        // an unresolvable bootstrap ordering (department needs a head who
        // needs a department). Application code is responsible for only ever
        // pointing DepartmentHeadId at an employee already in this department.
        builder.Property(d => d.DepartmentHeadId);

        builder.HasIndex(d => d.IsActive).HasDatabaseName("IX_Department_Active");
    }
}
