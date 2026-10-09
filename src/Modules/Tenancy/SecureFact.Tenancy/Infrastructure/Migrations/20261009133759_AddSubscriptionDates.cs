using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "plan_assigned_at",
                schema: "tenancy",
                table: "tenant",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reseller_assigned_at",
                schema: "tenancy",
                table: "tenant",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "allows_overage",
                schema: "tenancy",
                table: "plan",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The plan and the reseller that a tenant has were assigned when it was created, as far as anyone knows: the prices and the commission schedule that apply to it are those of that day.
            migrationBuilder.Sql("UPDATE tenancy.tenant SET plan_assigned_at = created_at, reseller_assigned_at = CASE WHEN reseller_id IS NULL THEN NULL ELSE created_at END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "plan_assigned_at",
                schema: "tenancy",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "reseller_assigned_at",
                schema: "tenancy",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "allows_overage",
                schema: "tenancy",
                table: "plan");
        }
    }
}
