using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserResellerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reseller_id",
                schema: "identity",
                table: "app_user",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reseller_id",
                schema: "identity",
                table: "app_user");
        }
    }
}
