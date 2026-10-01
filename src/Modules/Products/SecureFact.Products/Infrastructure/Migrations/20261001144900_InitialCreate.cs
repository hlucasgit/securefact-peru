using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Products.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "products");

            migrationBuilder.CreateTable(
                name: "product",
                schema: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    internal_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    unit_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    unit_value = table.Column<decimal>(type: "numeric(22,10)", precision: 22, scale: 10, nullable: false),
                    igv_affectation_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    sunat_product_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_tenant_id",
                schema: "products",
                table: "product",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_tenant_id_description",
                schema: "products",
                table: "product",
                columns: new[] { "tenant_id", "description" });

            migrationBuilder.CreateIndex(
                name: "IX_product_tenant_id_internal_code",
                schema: "products",
                table: "product",
                columns: new[] { "tenant_id", "internal_code" },
                unique: true);

            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            migrationBuilder.Sql(RlsSql.Enable("products", "product"));

            // Master data is never hard-deleted (documents keep their own snapshot); deactivation is an UPDATE.
            migrationBuilder.Sql(RlsSql.GrantToAppRole("products", "SELECT, INSERT, UPDATE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product",
                schema: "products");
        }
    }
}
