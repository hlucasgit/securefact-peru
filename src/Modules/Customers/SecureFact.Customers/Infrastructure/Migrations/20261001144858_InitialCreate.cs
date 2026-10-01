using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "customer",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    document_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_tenant_id",
                schema: "customers",
                table: "customer",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_tenant_id_document_type_code_document_number",
                schema: "customers",
                table: "customer",
                columns: new[] { "tenant_id", "document_type_code", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_tenant_id_name",
                schema: "customers",
                table: "customer",
                columns: new[] { "tenant_id", "name" });

            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            migrationBuilder.Sql(RlsSql.Enable("customers", "customer"));

            // Master data is never hard-deleted (documents keep their own snapshot); deactivation is an UPDATE.
            migrationBuilder.Sql(RlsSql.GrantToAppRole("customers", "SELECT, INSERT, UPDATE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer",
                schema: "customers");
        }
    }
}
