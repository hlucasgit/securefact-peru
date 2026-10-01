using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Organizations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "org");

            migrationBuilder.CreateTable(
                name: "company",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ruc = table.Column<string>(type: "character(11)", fixedLength: true, maxLength: 11, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    fiscal_address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    ubigeo = table.Column<string>(type: "character(6)", fixedLength: true, maxLength: 6, nullable: false),
                    tax_regime = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    contact_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    time_zone = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    default_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "establishment",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    ubigeo = table.Column<string>(type: "character(6)", fixedLength: true, maxLength: 6, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_establishment", x => x.id);
                    table.ForeignKey(
                        name: "FK_establishment_company_company_id",
                        column: x => x.company_id,
                        principalSchema: "org",
                        principalTable: "company",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_tenant_id",
                schema: "org",
                table: "company",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_company_tenant_id_ruc",
                schema: "org",
                table: "company",
                columns: new[] { "tenant_id", "ruc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_establishment_company_id_code",
                schema: "org",
                table: "establishment",
                columns: new[] { "company_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_establishment_tenant_id",
                schema: "org",
                table: "establishment",
                column: "tenant_id");

            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            foreach (var table in new[] { "company", "establishment" })
            {
                migrationBuilder.Sql(RlsSql.Enable("org", table));
            }

            // Business data: no platform-scope bypass. Companies and establishments are never hard-deleted.
            migrationBuilder.Sql(RlsSql.GrantToAppRole("org", "SELECT, INSERT, UPDATE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "establishment",
                schema: "org");

            migrationBuilder.DropTable(
                name: "company",
                schema: "org");
        }
    }
}
