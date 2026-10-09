using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Webhooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "webhook");

            migrationBuilder.CreateTable(
                name: "delivery",
                schema: "webhook",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "endpoint",
                schema: "webhook",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    events = table.Column<string[]>(type: "text[]", nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    secret_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_endpoint_id_created_at",
                schema: "webhook",
                table: "delivery",
                columns: new[] { "endpoint_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_endpoint_id_event_id",
                schema: "webhook",
                table: "delivery",
                columns: new[] { "endpoint_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_state_next_attempt_at",
                schema: "webhook",
                table: "delivery",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_tenant_id",
                schema: "webhook",
                table: "delivery",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_tenant_id",
                schema: "webhook",
                table: "endpoint",
                column: "tenant_id");

            migrationBuilder.Sql("""
                ALTER TABLE webhook.delivery ADD CONSTRAINT fk_delivery_endpoint FOREIGN KEY (endpoint_id) REFERENCES webhook.endpoint (id) ON DELETE CASCADE;
                ALTER TABLE webhook.delivery ADD CONSTRAINT ck_delivery_state CHECK (state IN ('Pending', 'Delivered', 'Failed', 'Dead'));
                ALTER TABLE webhook.endpoint ADD CONSTRAINT ck_endpoint_events CHECK (cardinality(events) > 0);
                """);

            // Each webhook and delivery belongs to its tenant. The dispatcher claims the deliveries that are due across tenants in the explicit platform scope and sends each one in the scope of its tenant.
            migrationBuilder.Sql(RlsSql.Enable("webhook", "endpoint", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.Enable("webhook", "delivery", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("webhook", "SELECT, INSERT, UPDATE, DELETE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery",
                schema: "webhook");

            migrationBuilder.DropTable(
                name: "endpoint",
                schema: "webhook");
        }
    }
}
