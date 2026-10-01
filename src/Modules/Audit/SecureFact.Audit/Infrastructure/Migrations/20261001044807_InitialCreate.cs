using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_event",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chain_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    old_values = table.Column<string>(type: "text", nullable: true),
                    new_values = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prev_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_chain_key_seq",
                schema: "audit",
                table: "audit_event",
                columns: new[] { "chain_key", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_entity_type_entity_id",
                schema: "audit",
                table: "audit_event",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_tenant_id",
                schema: "audit",
                table: "audit_event",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_tenant_id_action",
                schema: "audit",
                table: "audit_event",
                columns: new[] { "tenant_id", "action" });

            migrationBuilder.Sql(RlsSql.Enable("audit", "audit_event", RlsMode.TenantOrPlatform));
            // Append-only: the runtime role may only read and insert (ADR-003/§44).
            migrationBuilder.Sql(RlsSql.GrantToAppRole("audit", "SELECT, INSERT"));
            // Defence in depth: even a privileged role cannot silently rewrite history without first dropping these triggers.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'audit.audit_event is append-only (% is not allowed)', TG_OP USING ERRCODE = '42501';
                END
                $fn$;
                CREATE TRIGGER audit_event_no_update_delete BEFORE UPDATE OR DELETE ON audit.audit_event
                  FOR EACH ROW EXECUTE FUNCTION audit.forbid_mutation();
                CREATE TRIGGER audit_event_no_truncate BEFORE TRUNCATE ON audit.audit_event
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_event",
                schema: "audit");
        }
    }
}
