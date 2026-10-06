using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plan",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    max_companies = table.Column<int>(type: "integer", nullable: true),
                    max_users = table.Column<int>(type: "integer", nullable: true),
                    max_documents_per_month = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plan_code",
                schema: "tenancy",
                table: "plan",
                column: "code",
                unique: true);

            // The plan every tenant had until now: no limit at all, so nothing changes for existing tenants. xmin is a system column and is not created.
            migrationBuilder.Sql("""
                INSERT INTO tenancy.plan (id, code, name, max_companies, max_users, max_documents_per_month, is_active, created_at)
                VALUES ('0f1e2d3c-4b5a-4968-8776-655443322110', 'pilot', 'Piloto', NULL, NULL, NULL, true, now());
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "plan_id",
                schema: "tenancy",
                table: "tenant",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("0f1e2d3c-4b5a-4968-8776-655443322110"));
            migrationBuilder.Sql("ALTER TABLE tenancy.tenant ALTER COLUMN plan_id DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenancy.tenant ADD CONSTRAINT fk_tenant_plan FOREIGN KEY (plan_id) REFERENCES tenancy.plan (id);");

            migrationBuilder.Sql(RlsSql.EnableGlobalReference("tenancy", "plan"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("tenancy"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "plan_id",
                schema: "tenancy",
                table: "tenant");

            migrationBuilder.DropTable(
                name: "plan",
                schema: "tenancy");
        }
    }
}
