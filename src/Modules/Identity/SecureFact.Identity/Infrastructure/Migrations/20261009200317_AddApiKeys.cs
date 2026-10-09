using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_key",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_tenant_id",
                schema: "identity",
                table: "api_key",
                column: "tenant_id");

            // A key belongs to its tenant (the platform scope reads it only to authenticate a caller that is not yet identified); the secret is never stored, only its hash.
            migrationBuilder.Sql(RlsSql.Enable("identity", "api_key", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql("ALTER TABLE identity.api_key ADD CONSTRAINT ck_api_key_hash CHECK (octet_length(secret_hash) = 32);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key",
                schema: "identity");
        }
    }
}
