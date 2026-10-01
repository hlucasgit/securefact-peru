using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Catalogs.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "catalog_edition",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_number = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    loaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_edition", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_entry",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_number = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_entry", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_edition_catalog_number_source_sha256",
                schema: "catalog",
                table: "catalog_edition",
                columns: new[] { "catalog_number", "source_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_edition_catalog_number_version",
                schema: "catalog",
                table: "catalog_edition",
                columns: new[] { "catalog_number", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_entry_catalog_number_code_version",
                schema: "catalog",
                table: "catalog_entry",
                columns: new[] { "catalog_number", "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_entry_catalog_number_effective_from",
                schema: "catalog",
                table: "catalog_entry",
                columns: new[] { "catalog_number", "effective_from" });

            // Global reference data: readable by everyone, writable only in the explicit platform scope, and the runtime role
            // has no write privilege at all (catalogue loading is an owner-side operation).
            foreach (var table in new[] { "catalog_edition", "catalog_entry" })
            {
                migrationBuilder.Sql(RlsSql.EnableGlobalReference("catalog", table));
            }

            migrationBuilder.Sql(RlsSql.GrantToAppRole("catalog", "SELECT"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_edition",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_entry",
                schema: "catalog");
        }
    }
}
