using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "document",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    series_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    buyer_document_type = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    buyer_document_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    buyer_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    buyer_address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    buyer_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payable_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    totals = table.Column<string>(type: "jsonb", nullable: false),
                    original_request = table.Column<string>(type: "jsonb", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_key",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_key", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "series",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    establishment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series", x => x.id);
                    table.CheckConstraint("ck_series_last_number", "last_number >= 0 AND last_number <= 99999999");
                });

            migrationBuilder.CreateTable(
                name: "document_line",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    unit_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    product_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(22,10)", precision: 22, scale: 10, nullable: false),
                    unit_value = table.Column<decimal>(type: "numeric(22,10)", precision: 22, scale: 10, nullable: false),
                    affectation_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    line_extension_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    total_tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    unit_price_including_taxes = table.Column<decimal>(type: "numeric(22,10)", precision: 22, scale: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_line_document_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_tenant_id",
                schema: "billing",
                table: "document",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_tenant_id_company_id_document_type_code_series_cod~",
                schema: "billing",
                table: "document",
                columns: new[] { "tenant_id", "company_id", "document_type_code", "series_code", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_tenant_id_company_id_issue_date",
                schema: "billing",
                table: "document",
                columns: new[] { "tenant_id", "company_id", "issue_date" });

            migrationBuilder.CreateIndex(
                name: "IX_document_line_document_id_line_number",
                schema: "billing",
                table: "document_line",
                columns: new[] { "document_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_line_tenant_id",
                schema: "billing",
                table: "document_line",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_key_tenant_id",
                schema: "billing",
                table: "idempotency_key",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_key_tenant_id_key",
                schema: "billing",
                table: "idempotency_key",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_series_tenant_id",
                schema: "billing",
                table: "series",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_series_tenant_id_company_id_document_type_code_code",
                schema: "billing",
                table: "series",
                columns: new[] { "tenant_id", "company_id", "document_type_code", "code" },
                unique: true);

            foreach (var table in new[] { "series", "document", "document_line", "idempotency_key" })
            {
                migrationBuilder.Sql(RlsSql.Enable("billing", table));
            }

            // Fiscal data is insert-only: documents, lines and idempotency records can never be updated or deleted by the
            // runtime role. Only the series counter may be updated (atomic UPDATE … RETURNING in the numbering step).
            migrationBuilder.Sql(RlsSql.GrantToAppRole("billing", "SELECT, INSERT"));
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT UPDATE ON billing.series TO securefact_app;
                  END IF;
                END
                $grant$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_line",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "idempotency_key",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "series",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "document",
                schema: "billing");
        }
    }
}
