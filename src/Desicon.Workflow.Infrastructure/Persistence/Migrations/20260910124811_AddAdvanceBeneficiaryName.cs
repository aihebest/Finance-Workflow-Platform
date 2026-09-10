using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvanceBeneficiaryName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BeneficiaryName",
                table: "CashAdvanceRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BeneficiaryName",
                table: "CashAdvanceRequests");
        }
    }
}
