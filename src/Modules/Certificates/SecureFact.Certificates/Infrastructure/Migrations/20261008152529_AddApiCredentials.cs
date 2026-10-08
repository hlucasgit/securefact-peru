using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Certificates.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "api_client_id",
                schema: "certificates",
                table: "sol_credential",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "protected_api_client_secret",
                schema: "certificates",
                table: "sol_credential",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "api_client_id",
                schema: "certificates",
                table: "sol_credential");

            migrationBuilder.DropColumn(
                name: "protected_api_client_secret",
                schema: "certificates",
                table: "sol_credential");
        }
    }
}
