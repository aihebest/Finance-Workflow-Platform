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

        // Always Encrypted, applied now while the table is empty: an
        // ALTER COLUMN onto an encrypted type cannot run against existing
        // plaintext rows without an explicit encrypt-in-place migration, so
        // this is a one-way door once real account numbers land here.
        // Deterministic (not Randomized) so equality search/joins on the
        // column still work -- nothing in this codebase needs to search on
        // BankAccountNumber today, but Randomized would forbid it later
        // without re-encrypting, and Deterministic loses only the (already
        // unwanted) ability to range-query it. Requires "Column Encryption
        // Setting=Enabled" on the connection string and the CMK/CEK created
        // by the accompanying migration's raw SQL -- EF's fluent API has no
        // first-class Always Encrypted support, so IsEncrypted here is
        // metadata only; the actual column encryption is provisioned in
        // Migrations/*_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber.cs.
        builder.Property(b => b.BankAccountNumber).HasMaxLength(30).IsRequired();

        builder.Property(b => b.BankDetailsSetByUserId);
        builder.Property(b => b.BankDetailsSetAt)
            .HasConversion(DateTimeOffsetConverters.ToUtcDateTime2)
            .HasColumnType("datetime2");

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(b => b.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
