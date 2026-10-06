using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResellerBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "brand_name",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "logo",
                schema: "tenancy",
                table: "reseller",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_content_type",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_version",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_color",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "support_email",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_reseller_host",
                schema: "tenancy",
                table: "reseller",
                column: "host",
                unique: true,
                filter: "host IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_reseller_host",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "brand_name",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "host",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "logo",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "logo_content_type",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "logo_version",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "primary_color",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "support_email",
                schema: "tenancy",
                table: "reseller");
        }
    }
}
