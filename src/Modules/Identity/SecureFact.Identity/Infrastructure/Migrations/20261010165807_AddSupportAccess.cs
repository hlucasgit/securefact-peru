using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "support_grant_id",
                schema: "identity",
                table: "user_session",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "support_access_grant",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_access_grant", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_session_support_grant_id",
                schema: "identity",
                table: "user_session",
                column: "support_grant_id");

            migrationBuilder.CreateIndex(
                name: "IX_support_access_grant_tenant_id",
                schema: "identity",
                table: "support_access_grant",
                column: "tenant_id");

            // The authorization is the account's: only its tenant writes it. The platform scope reads it (to know which accounts may be entered) but can neither give nor extend one.
            migrationBuilder.Sql("""
                ALTER TABLE identity.support_access_grant ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.support_access_grant FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation ON identity.support_access_grant;
                CREATE POLICY tenant_isolation ON identity.support_access_grant
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR current_setting('app.scope', true) = 'platform')
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "support_access_grant",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_user_session_support_grant_id",
                schema: "identity",
                table: "user_session");

            migrationBuilder.DropColumn(
                name: "support_grant_id",
                schema: "identity",
                table: "user_session");
        }
    }
}
