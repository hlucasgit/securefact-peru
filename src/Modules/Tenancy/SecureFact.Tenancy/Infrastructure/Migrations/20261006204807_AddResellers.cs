using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResellers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reseller_id",
                schema: "tenancy",
                table: "plan",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reseller",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reseller", x => x.id);
                });

            // reseller_id of a tenant existed before resellers did and nothing could set it; clear any stray value so that the key can be added. xmin is a system column and is not created.
            migrationBuilder.Sql("UPDATE tenancy.tenant SET reseller_id = NULL WHERE reseller_id IS NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE tenancy.tenant ADD CONSTRAINT fk_tenant_reseller FOREIGN KEY (reseller_id) REFERENCES tenancy.reseller (id);");
            migrationBuilder.Sql("ALTER TABLE tenancy.plan ADD CONSTRAINT fk_plan_reseller FOREIGN KEY (reseller_id) REFERENCES tenancy.reseller (id);");
            migrationBuilder.Sql("CREATE INDEX ix_tenant_reseller ON tenancy.tenant (reseller_id) WHERE reseller_id IS NOT NULL;");

            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("tenancy", "reseller"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("tenancy"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS tenancy.ix_tenant_reseller;");
            migrationBuilder.Sql("ALTER TABLE tenancy.tenant DROP CONSTRAINT IF EXISTS fk_tenant_reseller;");

            migrationBuilder.DropColumn(
                name: "reseller_id",
                schema: "tenancy",
                table: "plan");

            migrationBuilder.DropTable(
                name: "reseller",
                schema: "tenancy");
        }
    }
}
