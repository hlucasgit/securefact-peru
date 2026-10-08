using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDedupeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dedupe_key",
                schema: "notifications",
                table: "email_queue",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_email_queue_dedupe",
                schema: "notifications",
                table: "email_queue",
                column: "dedupe_key",
                unique: true,
                filter: "dedupe_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_email_queue_dedupe",
                schema: "notifications",
                table: "email_queue");

            migrationBuilder.DropColumn(
                name: "dedupe_key",
                schema: "notifications",
                table: "email_queue");
        }
    }
}
