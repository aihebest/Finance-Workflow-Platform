using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_RequestId",
                table: "AuditEvents");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "AuditEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    OutboxMessageId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Template = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecipientRolesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.OutboxMessageId);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_AuditEvent_Idempotency",
                table: "AuditEvents",
                columns: new[] { "RequestId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Pending",
                table: "OutboxMessages",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_RequestId",
                table: "OutboxMessages",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "UQ_AuditEvent_Idempotency",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "AuditEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RequestId",
                table: "AuditEvents",
                column: "RequestId");
        }
    }
}
