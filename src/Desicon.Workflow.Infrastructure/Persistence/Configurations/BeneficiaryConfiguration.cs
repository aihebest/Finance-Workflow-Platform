using Desicon.Workflow.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Workflow.Infrastructure.Persistence.Configurations;

public sealed class BeneficiaryConfiguration : IEntityTypeConfiguration<Beneficiary>
{
    public void Configure(EntityTypeBuilder<Beneficiary> builder)
    {
        builder.ToTable("Beneficiaries", t => t.HasCheckConstraint(
            "CK_Beneficiary_EmployeeLink",
            "([Type] <> 'Employee' OR [EmployeeId] IS NOT NULL)"));

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(200).IsRequired();
        builder.Property(b => b.BankName).HasMaxLength(100).IsRequired();
        builder.Property(b => b.BankAccountNumber).HasMaxLength(30).IsRequired();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(b => b.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
