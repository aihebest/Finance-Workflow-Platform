using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBcDocumentNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BcDocumentNumber",
                table: "Requests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Request_BcDocumentNumber",
                table: "Requests",
                column: "BcDocumentNumber",
                filter: "[BcDocumentNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Request_BcDocumentNumber",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "BcDocumentNumber",
                table: "Requests");
        }
    }
}
