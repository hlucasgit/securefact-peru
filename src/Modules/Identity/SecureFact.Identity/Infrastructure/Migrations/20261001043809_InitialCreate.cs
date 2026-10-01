using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "app_user",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    email_normalized = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mfa_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mfa_secret = table.Column<byte[]>(type: "bytea", nullable: true),
                    mfa_last_step = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_token",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_token", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_session",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refresh_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    replaced_by = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_session", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_role",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_role", x => new { x.user_id, x.role_code });
                    table.ForeignKey(
                        name: "FK_user_role_app_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_user_email_normalized",
                schema: "identity",
                table: "app_user",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_user_tenant_id",
                schema: "identity",
                table: "app_user",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_token_tenant_id",
                schema: "identity",
                table: "password_reset_token",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_token_token_hash",
                schema: "identity",
                table: "password_reset_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_token_user_id",
                schema: "identity",
                table: "password_reset_token",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_role_tenant_id",
                schema: "identity",
                table: "user_role",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_session_family_id",
                schema: "identity",
                table: "user_session",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_session_refresh_hash",
                schema: "identity",
                table: "user_session",
                column: "refresh_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_session_tenant_id",
                schema: "identity",
                table: "user_session",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_session_user_id",
                schema: "identity",
                table: "user_session",
                column: "user_id");

            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            // tenant_id is NULL for platform staff, who are visible only in an explicit platform scope (ADR-003).
            foreach (var table in new[] { "app_user", "user_role", "user_session", "password_reset_token" })
            {
                migrationBuilder.Sql(RlsSql.Enable("identity", table, RlsMode.TenantOrPlatform));
            }

            migrationBuilder.Sql(RlsSql.GrantToAppRole("identity", "SELECT, INSERT, UPDATE, DELETE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_token",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_role",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_session",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "app_user",
                schema: "identity");
        }
    }
}
