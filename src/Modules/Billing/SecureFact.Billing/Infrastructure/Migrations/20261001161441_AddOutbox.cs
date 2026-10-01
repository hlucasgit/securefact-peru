using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_message", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "billing",
                table: "outbox_message",
                column: "next_attempt_at",
                filter: "processed_at IS NULL AND dead_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_tenant_id",
                schema: "billing",
                table: "outbox_message",
                column: "tenant_id");
            // Platform scope may claim messages across tenants; every other access stays tenant-isolated.
            migrationBuilder.Sql(RlsSql.Enable("billing", "outbox_message", RlsMode.TenantOrPlatform));

            // Insert-only for the payload; the runtime role may update only the delivery bookkeeping and never delete.
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT SELECT, INSERT ON billing.outbox_message TO securefact_app;
                    GRANT UPDATE (attempts, next_attempt_at, locked_until, processed_at, dead_at, last_error) ON billing.outbox_message TO securefact_app;
                  END IF;
                END
                $grant$;
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION billing.guard_outbox_message() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'billing.outbox_message is append-only (DELETE is not allowed)' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.id IS DISTINCT FROM OLD.id
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.event_type IS DISTINCT FROM OLD.event_type
                     OR NEW.payload IS DISTINCT FROM OLD.payload
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'billing.outbox_message: the event is immutable; only delivery bookkeeping changes' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER outbox_message_guard BEFORE UPDATE OR DELETE ON billing.outbox_message
                  FOR EACH ROW EXECUTE FUNCTION billing.guard_outbox_message();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "billing");
        }
    }
}
