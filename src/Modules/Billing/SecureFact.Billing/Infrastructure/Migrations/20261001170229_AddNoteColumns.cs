using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "billing",
                table: "document",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                schema: "billing",
                table: "document",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "referenced_document_id",
                schema: "billing",
                table: "document",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "referenced_document_type",
                schema: "billing",
                table: "document",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "referenced_number",
                schema: "billing",
                table: "document",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "referenced_series",
                schema: "billing",
                table: "document",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_referenced_document_id",
                schema: "billing",
                table: "document",
                column: "referenced_document_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_referenced_document_id",
                schema: "billing",
                table: "document");

            migrationBuilder.DropColumn(
                name: "reason",
                schema: "billing",
                table: "document");

            migrationBuilder.DropColumn(
                name: "reason_code",
                schema: "billing",
                table: "document");

            migrationBuilder.DropColumn(
                name: "referenced_document_id",
                schema: "billing",
                table: "document");

            migrationBuilder.DropColumn(
                name: "referenced_document_type",
                schema: "billing",
                table: "document");

            migrationBuilder.DropColumn(
                name: "referenced_number",
                schema: "billing",
                table: "document");

            migrationBuilder.DropColumn(
                name: "referenced_series",
                schema: "billing",
                table: "document");
        }
    }
}
