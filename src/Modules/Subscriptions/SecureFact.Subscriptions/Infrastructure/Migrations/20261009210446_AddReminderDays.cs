using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Subscriptions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "reminder_days",
                schema: "subscription",
                table: "billing_policy",
                type: "integer",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reminder_days",
                schema: "subscription",
                table: "billing_policy");
        }
    }
}
