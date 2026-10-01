using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Certificates.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "certificates");

            migrationBuilder.CreateTable(
                name: "company_certificate",
                schema: "certificates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    thumbprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    protected_pfx = table.Column<byte[]>(type: "bytea", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    ruc_in_subject = table.Column<bool>(type: "boolean", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_certificate", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_certificate_tenant_id",
                schema: "certificates",
                table: "company_certificate",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_company_certificate_tenant_id_company_id_thumbprint",
                schema: "certificates",
                table: "company_certificate",
                columns: new[] { "tenant_id", "company_id", "thumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_company_certificate_active",
                schema: "certificates",
                table: "company_certificate",
                columns: new[] { "tenant_id", "company_id" },
                unique: true,
                filter: "is_active");

            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            migrationBuilder.Sql(RlsSql.Enable("certificates", "company_certificate"));

            // Certificates are never hard-deleted (signed documents must stay verifiable); deactivation is an UPDATE.
            migrationBuilder.Sql(RlsSql.GrantToAppRole("certificates", "SELECT, INSERT, UPDATE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_certificate",
                schema: "certificates");
        }
    }
}
