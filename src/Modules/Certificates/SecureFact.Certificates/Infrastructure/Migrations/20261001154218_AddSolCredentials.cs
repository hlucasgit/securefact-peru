using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Certificates.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSolCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sol_credential",
                schema: "certificates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sol_user = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    protected_password = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sol_credential", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sol_credential_tenant_id",
                schema: "certificates",
                table: "sol_credential",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_sol_credential_tenant_id_company_id",
                schema: "certificates",
                table: "sol_credential",
                columns: new[] { "tenant_id", "company_id" },
                unique: true);
            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            migrationBuilder.Sql(RlsSql.Enable("certificates", "sol_credential"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("certificates", "SELECT, INSERT, UPDATE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sol_credential",
                schema: "certificates");
        }
    }
}
