using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResellerDomainVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "host_checked_at",
                schema: "tenancy",
                table: "reseller",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_error",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "host_failures",
                schema: "tenancy",
                table: "reseller",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "host_status",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "host_token",
                schema: "tenancy",
                table: "reseller",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "host_verified_at",
                schema: "tenancy",
                table: "reseller",
                type: "timestamp with time zone",
                nullable: true);
            // A reseller that already had a domain (ADR-044) was serving its portal there, so it stays verified: nobody is taken down by this change. Its proof is new, for the day it is checked.
            migrationBuilder.Sql("""
                UPDATE tenancy.reseller
                SET host_status = 'Verified', host_verified_at = now(), host_checked_at = NULL,
                    host_token = replace(gen_random_uuid()::text, '-', '') || substr(replace(gen_random_uuid()::text, '-', ''), 1, 16)
                WHERE host IS NOT NULL;
                UPDATE tenancy.reseller SET host_status = 'None' WHERE host IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "host_checked_at",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "host_error",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "host_failures",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "host_status",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "host_token",
                schema: "tenancy",
                table: "reseller");

            migrationBuilder.DropColumn(
                name: "host_verified_at",
                schema: "tenancy",
                table: "reseller");
        }
    }
}
