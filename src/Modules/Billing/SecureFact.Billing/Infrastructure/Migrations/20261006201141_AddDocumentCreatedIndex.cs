using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCreatedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_document_tenant_created",
                schema: "billing",
                table: "document",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_tenant_created",
                schema: "billing",
                table: "document");
        }
    }
}
