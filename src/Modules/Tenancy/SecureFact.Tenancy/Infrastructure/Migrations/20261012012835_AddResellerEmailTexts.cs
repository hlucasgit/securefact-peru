using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResellerEmailTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email_closing",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_greeting",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email_closing",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "email_greeting",
                schema: "tenancy",
                table: "reseller");
        }
    }
}
