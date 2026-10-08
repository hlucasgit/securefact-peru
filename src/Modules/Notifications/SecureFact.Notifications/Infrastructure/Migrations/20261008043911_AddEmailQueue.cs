using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "email_queue",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    to_address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    text_body = table.Column<string>(type: "text", nullable: false),
                    html_body = table.Column<string>(type: "text", nullable: false),
                    from_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    reply_to = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_queue", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_queue_pending",
                schema: "notifications",
                table: "email_queue",
                column: "next_attempt_at",
                filter: "sent_at IS NULL AND dead_at IS NULL");

            // The queue belongs to the platform: only the explicit platform scope sees it. The dispatcher also deletes what it already sent (the retention purge).
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("notifications", "email_queue"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("notifications", "SELECT, INSERT, UPDATE, DELETE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_queue",
                schema: "notifications");
        }
    }
}
