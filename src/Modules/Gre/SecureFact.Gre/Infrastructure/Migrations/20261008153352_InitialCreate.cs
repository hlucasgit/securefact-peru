using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Gre.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gre");

            migrationBuilder.CreateTable(
                name: "guide",
                schema: "gre",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    motive_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    modality_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    recipient_document = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    request = table.Column<string>(type: "jsonb", nullable: false),
                    file_base_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    signed_xml = table.Column<string>(type: "text", nullable: false),
                    digest_value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ticket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cdr_zip = table.Column<byte[]>(type: "bytea", nullable: true),
                    cdr_process_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cdr_response_code = table.Column<int>(type: "integer", nullable: true),
                    cdr_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cdr_observations = table.Column<string>(type: "jsonb", nullable: false),
                    error_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guide", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "series",
                schema: "gre",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guide_state_next_attempt_at",
                schema: "gre",
                table: "guide",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_guide_tenant_id",
                schema: "gre",
                table: "guide",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_guide_tenant_id_company_id_series_number",
                schema: "gre",
                table: "guide",
                columns: new[] { "tenant_id", "company_id", "series", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guide_tenant_id_created_at",
                schema: "gre",
                table: "guide",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_series_tenant_id",
                schema: "gre",
                table: "series",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_series_tenant_id_company_id_code",
                schema: "gre",
                table: "series",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true);

            // xmin is a PostgreSQL system column and is intentionally not created: it backs the row-version token.
            migrationBuilder.Sql(RlsSql.Enable("gre", "series", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.Enable("gre", "guide", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("gre", "SELECT, INSERT"));
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT UPDATE ON gre.series, gre.guide TO securefact_app;
                  END IF;
                END
                $grant$;
                """);

            // A guide never disappears, its signed document never changes and, once SUNAT answers (or the send failed for good), nothing changes at all, even for the owner of the application code.
            migrationBuilder.Sql("""
                CREATE FUNCTION gre.forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION '% is not allowed on %.%', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
                END
                $fn$;

                CREATE FUNCTION gre.guard_guide() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.state IN ('Accepted', 'AcceptedWithObservations', 'Rejected', 'Failed') THEN
                    RAISE EXCEPTION 'gre.guide: a guide with a final answer is immutable' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.signed_xml IS DISTINCT FROM OLD.signed_xml
                     OR NEW.digest_value IS DISTINCT FROM OLD.digest_value
                     OR NEW.request IS DISTINCT FROM OLD.request
                     OR NEW.series IS DISTINCT FROM OLD.series
                     OR NEW.number IS DISTINCT FROM OLD.number
                     OR NEW.issue_date IS DISTINCT FROM OLD.issue_date
                     OR NEW.company_id IS DISTINCT FROM OLD.company_id
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.file_base_name IS DISTINCT FROM OLD.file_base_name THEN
                    RAISE EXCEPTION 'gre.guide: the signed guide is immutable' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE FUNCTION gre.guard_series() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.last_number < OLD.last_number OR NEW.code IS DISTINCT FROM OLD.code OR NEW.company_id IS DISTINCT FROM OLD.company_id THEN
                    RAISE EXCEPTION 'gre.series: the numbering only moves forward' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER guide_guard BEFORE UPDATE ON gre.guide
                  FOR EACH ROW EXECUTE FUNCTION gre.guard_guide();
                CREATE TRIGGER guide_no_delete BEFORE DELETE ON gre.guide
                  FOR EACH ROW EXECUTE FUNCTION gre.forbid_mutation();
                CREATE TRIGGER series_guard BEFORE UPDATE ON gre.series
                  FOR EACH ROW EXECUTE FUNCTION gre.guard_series();
                CREATE TRIGGER series_no_delete BEFORE DELETE ON gre.series
                  FOR EACH ROW EXECUTE FUNCTION gre.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guide",
                schema: "gre");

            migrationBuilder.DropTable(
                name: "series",
                schema: "gre");
        }
    }
}
