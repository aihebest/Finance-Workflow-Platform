using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ModuleKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FormCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FormRevision = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TreasuryNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    JournalVoucherNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CurrentState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CurrentActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StateEnteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SlaDueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EscalationCount = table.Column<int>(type: "int", nullable: false),
                    ReminderCount = table.Column<int>(type: "int", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    TotalAmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.RequestId);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    AuditEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FromState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ToState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OnBehalfOfUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClientIpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    EventHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.AuditEventId);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashAdvanceRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AllocationType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProjectCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CostCentreCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    StationScope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HasSupportingDocuments = table.Column<bool>(type: "bit", nullable: false),
                    AmountInWords = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CashReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetirementDueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetirementStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetiredAmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAdvanceRequests", x => x.RequestId);
                    table.CheckConstraint("CK_Advance_Allocation", "(([AllocationType] = 'Project' AND [ProjectCode] IS NOT NULL) OR ([AllocationType] = 'CostCentre' AND [CostCentreCode] IS NOT NULL))");
                    table.CheckConstraint("CK_MakerChecker_Advance", "([PostedByUserId] IS NULL OR [AuthorisedByUserId] IS NULL OR [PostedByUserId] <> [AuthorisedByUserId])");
                    table.CheckConstraint("CK_RetirementDue", "([RetirementDueDate] IS NULL OR ([CashReleasedAt] IS NOT NULL AND [RetirementDueDate] >= [CashReleasedAt]))");
                    table.ForeignKey(
                        name: "FK_CashAdvanceRequests_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlPostingLines",
                columns: table => new
                {
                    PostingLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Side = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AccountNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Narration = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlPostingLines", x => x.PostingLineId);
                    table.ForeignKey(
                        name: "FK_GlPostingLines_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdvanceLines",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CurrencyCode = table.Column<string>(type: "char(3)", fixedLength: true, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    FxRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FxRateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvanceLines", x => x.LineId);
                    table.CheckConstraint("CK_Currency_AdvanceLine", "([CurrencyCode] <> 'NGN' OR [FxRate] = 1.0)");
                    table.ForeignKey(
                        name: "FK_AdvanceLines_CashAdvanceRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "CashAdvanceRequests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BeneficiaryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RetiresAdvanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdvanceAmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReceiptStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AmountInWords = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PaymentDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefundReceivedAmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseRequests", x => x.RequestId);
                    table.CheckConstraint("CK_MakerChecker_Expense", "([PostedByUserId] IS NULL OR [AuthorisedByUserId] IS NULL OR [PostedByUserId] <> [AuthorisedByUserId])");
                    table.ForeignKey(
                        name: "FK_ExpenseRequests_CashAdvanceRequests_RetiresAdvanceId",
                        column: x => x.RetiresAdvanceId,
                        principalTable: "CashAdvanceRequests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseRequests_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseLines",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpenseCategoryId = table.Column<int>(type: "int", nullable: true),
                    ProjectCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CostCentreCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CurrencyCode = table.Column<string>(type: "char(3)", fixedLength: true, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    FxRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FxRateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountNgn = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseLines", x => x.LineId);
                    table.CheckConstraint("CK_Currency_ExpenseLine", "([CurrencyCode] <> 'NGN' OR [FxRate] = 1.0)");
                    table.CheckConstraint("CK_ExpenseLine_Allocation", "(([ProjectCode] IS NOT NULL AND [CostCentreCode] IS NULL) OR ([ProjectCode] IS NULL AND [CostCentreCode] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_ExpenseLines_ExpenseRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ExpenseRequests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvanceLines_RequestId",
                table: "AdvanceLines",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RequestId",
                table: "AuditEvents",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Advance_Overdue",
                table: "CashAdvanceRequests",
                columns: new[] { "RetirementStatus", "RetirementDueDate" })
                .Annotation("SqlServer:Include", new[] { "RetiredAmountNgn" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLines_RequestId",
                table: "ExpenseLines",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_RetiresAdvanceId",
                table: "ExpenseRequests",
                column: "RetiresAdvanceId");

            migrationBuilder.CreateIndex(
                name: "IX_GlPostingLines_RequestId",
                table: "GlPostingLines",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Request_Actor_State",
                table: "Requests",
                columns: new[] { "CurrentActorId", "CurrentState" })
                .Annotation("SqlServer:Include", new[] { "RequestNumber", "SlaDueAt", "TotalAmountNgn" });

            migrationBuilder.CreateIndex(
                name: "IX_Request_Ageing",
                table: "Requests",
                columns: new[] { "ModuleKey", "CurrentState", "SubmittedAt" })
                .Annotation("SqlServer:Include", new[] { "TotalAmountNgn", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Request_Sla_Breach",
                table: "Requests",
                column: "SlaDueAt",
                filter: "[ClosedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_RequestNumber",
                table: "Requests",
                column: "RequestNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvanceLines");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ExpenseLines");

            migrationBuilder.DropTable(
                name: "GlPostingLines");

            migrationBuilder.DropTable(
                name: "ExpenseRequests");

            migrationBuilder.DropTable(
                name: "CashAdvanceRequests");

            migrationBuilder.DropTable(
                name: "Requests");
        }
    }
}
