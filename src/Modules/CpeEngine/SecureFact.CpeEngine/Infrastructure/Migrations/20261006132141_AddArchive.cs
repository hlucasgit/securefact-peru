using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Messaging;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.CpeEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archived_file",
                schema: "cpe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    electronic_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(900)", maxLength: 900, nullable: false),
                    version_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archived_file", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "cpe",
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
                name: "IX_archived_file_electronic_document_id_kind",
                schema: "cpe",
                table: "archived_file",
                columns: new[] { "electronic_document_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_archived_file_tenant_id",
                schema: "cpe",
                table: "archived_file",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "cpe",
                table: "outbox_message",
                column: "next_attempt_at",
                filter: "processed_at IS NULL AND dead_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_tenant_id",
                schema: "cpe",
                table: "outbox_message",
                column: "tenant_id");

            // Tenant isolation for both tables; the outbox also admits the platform scope, which the dispatcher uses to claim messages across tenants.
            migrationBuilder.Sql(RlsSql.Enable("cpe", "archived_file"));
            migrationBuilder.Sql(RlsSql.Enable("cpe", "outbox_message", RlsMode.TenantOrPlatform));

            // The archive register is insert-only: an archived file is never replaced or forgotten, and its recorded hash is what later reads are checked against.
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT SELECT, INSERT ON cpe.archived_file TO securefact_app;
                  END IF;
                END
                $grant$;

                CREATE FUNCTION cpe.guard_archived_file() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'cpe.archived_file is insert-only (an archived file is never changed or removed)' USING ERRCODE = '42501';
                END
                $fn$;

                CREATE TRIGGER archived_file_guard BEFORE UPDATE OR DELETE ON cpe.archived_file
                  FOR EACH ROW EXECUTE FUNCTION cpe.guard_archived_file();
                """);

            // The outbox of the module: grants, immutable events, delete only through the purge function (ADR-022, ADR-035).
            migrationBuilder.Sql(OutboxSql.Secure("cpe"));

            // The reconciliation of the archive asks whether an event of a kind for a document is already in the queue.
            migrationBuilder.Sql("CREATE INDEX ix_cpe_outbox_message_document ON cpe.outbox_message (event_type, (payload ->> 'electronicDocumentId'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archived_file",
                schema: "cpe");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "cpe");

            migrationBuilder.Sql("""
                DROP FUNCTION cpe.guard_archived_file();
                DROP FUNCTION cpe.guard_outbox_message();
                DROP FUNCTION cpe.purge_outbox_messages(interval);
                """);
        }
    }
}
