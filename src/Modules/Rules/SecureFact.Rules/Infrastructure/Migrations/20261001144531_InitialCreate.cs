using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Rules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rules");

            migrationBuilder.CreateTable(
                name: "rule_version",
                schema: "rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    source = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    verification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_version", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rule_version_code_effective_from",
                schema: "rules",
                table: "rule_version",
                columns: new[] { "code", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "IX_rule_version_code_version",
                schema: "rules",
                table: "rule_version",
                columns: new[] { "code", "version" },
                unique: true);

            // Global regulatory data: readable by everyone, writable only by the schema owner in the explicit platform scope.
            migrationBuilder.Sql(RlsSql.EnableGlobalReference("rules", "rule_version"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("rules", "SELECT"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rule_version",
                schema: "rules");
        }
    }
}
