using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleSecurityAndRequestForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DepartmentHeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    SecurityEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModuleKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FromState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AttemptedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnBehalfOfUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.SecurityEventId);
                    table.ForeignKey(
                        name: "FK_SecurityEvents_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntraObjectId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StaffNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Grade = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    LineManagerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultCostCentreCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employees_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Employees_Employees_LineManagerId",
                        column: x => x.LineManagerId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Beneficiaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BankAccountNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Beneficiaries", x => x.Id);
                    table.CheckConstraint("CK_Beneficiary_EmployeeLink", "([Type] <> 'Employee' OR [EmployeeId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Beneficiaries_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Delegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                    table.CheckConstraint("CK_Delegation_Window", "([EndsAt] >= [StartsAt])");
                    table.ForeignKey(
                        name: "FK_Delegations_Employees_FromEmployeeId",
                        column: x => x.FromEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delegations_Employees_ToEmployeeId",
                        column: x => x.ToEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_DepartmentId",
                table: "Requests",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_RequesterId",
                table: "Requests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_BeneficiaryId",
                table: "ExpenseRequests",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_Beneficiaries_EmployeeId",
                table: "Beneficiaries",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegation_Active_From",
                table: "Delegations",
                columns: new[] { "IsActive", "FromEmployeeId", "StartsAt", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_FromEmployeeId",
                table: "Delegations",
                column: "FromEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_ToEmployeeId",
                table: "Delegations",
                column: "ToEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Department_Active",
                table: "Departments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UQ_Department_Code",
                table: "Departments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employee_Department_Active",
                table: "Employees",
                columns: new[] { "DepartmentId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Employee_LineManager",
                table: "Employees",
                column: "LineManagerId");

            migrationBuilder.CreateIndex(
                name: "UQ_Employee_EntraObjectId",
                table: "Employees",
                column: "EntraObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Employee_StaffNumber",
                table: "Employees",
                column: "StaffNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_Actor",
                table: "SecurityEvents",
                columns: new[] { "AttemptedByUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_Request",
                table: "SecurityEvents",
                columns: new[] { "RequestId", "OccurredAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseRequests_Beneficiaries_BeneficiaryId",
                table: "ExpenseRequests",
                column: "BeneficiaryId",
                principalTable: "Beneficiaries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Requests_Departments_DepartmentId",
                table: "Requests",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Requests_Employees_CurrentActorId",
                table: "Requests",
                column: "CurrentActorId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Requests_Employees_RequesterId",
                table: "Requests",
                column: "RequesterId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseRequests_Beneficiaries_BeneficiaryId",
                table: "ExpenseRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Requests_Departments_DepartmentId",
                table: "Requests");

            migrationBuilder.DropForeignKey(
                name: "FK_Requests_Employees_CurrentActorId",
                table: "Requests");

            migrationBuilder.DropForeignKey(
                name: "FK_Requests_Employees_RequesterId",
                table: "Requests");

            migrationBuilder.DropTable(
                name: "Beneficiaries");

            migrationBuilder.DropTable(
                name: "Delegations");

            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_Requests_DepartmentId",
                table: "Requests");

            migrationBuilder.DropIndex(
                name: "IX_Requests_RequesterId",
                table: "Requests");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_BeneficiaryId",
                table: "ExpenseRequests");
        }
    }
}
