using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Organizations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDetractionAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detraction_account",
                schema: "org",
                table: "company",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "detraction_account",
                schema: "org",
                table: "company");
        }
    }
}
