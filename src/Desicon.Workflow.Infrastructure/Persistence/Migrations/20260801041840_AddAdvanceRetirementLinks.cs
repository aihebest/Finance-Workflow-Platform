using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvanceRetirementLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdvanceRetirementLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpenseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashAdvanceRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountAppliedNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvanceRetirementLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdvanceRetirementLinks_Requests_CashAdvanceRequestId",
                        column: x => x.CashAdvanceRequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AdvanceRetirementLinks_Requests_ExpenseRequestId",
                        column: x => x.ExpenseRequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvanceRetirementLink_CashAdvanceRequest",
                table: "AdvanceRetirementLinks",
                column: "CashAdvanceRequestId");

            migrationBuilder.CreateIndex(
                name: "UQ_AdvanceRetirementLink_ExpenseRequest",
                table: "AdvanceRetirementLinks",
                column: "ExpenseRequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvanceRetirementLinks");
        }
    }
}
