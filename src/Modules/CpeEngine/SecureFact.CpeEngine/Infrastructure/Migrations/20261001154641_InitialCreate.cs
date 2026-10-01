using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.CpeEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cpe");

            migrationBuilder.CreateTable(
                name: "electronic_document",
                schema: "cpe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    series = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    file_base_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    signed_xml = table.Column<string>(type: "text", nullable: false),
                    digest_value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ticket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cdr_zip = table.Column<byte[]>(type: "bytea", nullable: true),
                    cdr_process_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cdr_response_code = table.Column<int>(type: "integer", nullable: true),
                    cdr_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cdr_observations = table.Column<string>(type: "jsonb", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_electronic_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "electronic_document_event",
                schema: "cpe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    electronic_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    to_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_electronic_document_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_electronic_document_state_next_attempt_at",
                schema: "cpe",
                table: "electronic_document",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_electronic_document_tenant_id",
                schema: "cpe",
                table: "electronic_document",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_electronic_document_tenant_id_document_id",
                schema: "cpe",
                table: "electronic_document",
                columns: new[] { "tenant_id", "document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_electronic_document_event_electronic_document_id_occurred_at",
                schema: "cpe",
                table: "electronic_document_event",
                columns: new[] { "electronic_document_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_electronic_document_event_tenant_id",
                schema: "cpe",
                table: "electronic_document_event",
                column: "tenant_id");
            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            migrationBuilder.Sql(RlsSql.Enable("cpe", "electronic_document", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.Enable("cpe", "electronic_document_event"));

            // The history is insert-only; only the electronic document itself is updated (state, CDR, retry bookkeeping).
            migrationBuilder.Sql(RlsSql.GrantToAppRole("cpe", "SELECT, INSERT"));
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT UPDATE ON cpe.electronic_document TO securefact_app;
                  END IF;
                END
                $grant$;
                """);

            // Accepted/rejected documents and the signed XML are immutable, even for the owner of the application code.
            migrationBuilder.Sql("""
                CREATE FUNCTION cpe.forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION '% is not allowed on %.%', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
                END
                $fn$;

                CREATE FUNCTION cpe.guard_electronic_document() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.state IN ('Accepted', 'AcceptedWithObservations', 'Rejected') THEN
                    RAISE EXCEPTION 'cpe.electronic_document: a document with a final SUNAT answer is immutable' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.signed_xml IS DISTINCT FROM OLD.signed_xml
                     OR NEW.digest_value IS DISTINCT FROM OLD.digest_value
                     OR NEW.document_id IS DISTINCT FROM OLD.document_id
                     OR NEW.company_id IS DISTINCT FROM OLD.company_id
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.file_base_name IS DISTINCT FROM OLD.file_base_name THEN
                    RAISE EXCEPTION 'cpe.electronic_document: the signed document is immutable' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER electronic_document_guard BEFORE UPDATE ON cpe.electronic_document
                  FOR EACH ROW EXECUTE FUNCTION cpe.guard_electronic_document();
                CREATE TRIGGER electronic_document_no_delete BEFORE DELETE ON cpe.electronic_document
                  FOR EACH ROW EXECUTE FUNCTION cpe.forbid_mutation();
                CREATE TRIGGER electronic_document_event_no_update_delete BEFORE UPDATE OR DELETE ON cpe.electronic_document_event
                  FOR EACH ROW EXECUTE FUNCTION cpe.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "electronic_document",
                schema: "cpe");

            migrationBuilder.DropTable(
                name: "electronic_document_event",
                schema: "cpe");
        }
    }
}
