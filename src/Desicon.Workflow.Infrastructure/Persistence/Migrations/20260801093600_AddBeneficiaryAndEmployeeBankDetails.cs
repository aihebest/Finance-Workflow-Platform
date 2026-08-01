using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBeneficiaryAndEmployeeBankDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BeneficiaryHasBankDetails",
                table: "ExpenseRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BankAccountNumber",
                table: "Employees",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Employees",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentMethod",
                table: "CashAdvanceRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BankDetailsSetAt",
                table: "Beneficiaries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BankDetailsSetByUserId",
                table: "Beneficiaries",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BeneficiaryHasBankDetails",
                table: "ExpenseRequests");

            migrationBuilder.DropColumn(
                name: "BankAccountNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "CashAdvanceRequests");

            migrationBuilder.DropColumn(
                name: "BankDetailsSetAt",
                table: "Beneficiaries");

            migrationBuilder.DropColumn(
                name: "BankDetailsSetByUserId",
                table: "Beneficiaries");
        }
    }
}
