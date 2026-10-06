using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSuspendedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "suspended_by",
                schema: "tenancy",
                table: "tenant",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Until now only the platform could suspend a tenant, so every suspension that exists is the platform's and a reseller cannot lift it.
            migrationBuilder.Sql("UPDATE tenancy.tenant SET suspended_by = 'Platform' WHERE status = 'Suspended';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "suspended_by",
                schema: "tenancy",
                table: "tenant");
        }
    }
}
