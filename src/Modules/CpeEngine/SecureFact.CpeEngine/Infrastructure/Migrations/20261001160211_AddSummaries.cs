using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.CpeEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "issue_date",
                schema: "cpe",
                table: "electronic_document",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.CreateTable(
                name: "summary_item",
                schema: "cpe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    summary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    electronic_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_summary_item", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_electronic_document_tenant_id_file_base_name",
                schema: "cpe",
                table: "electronic_document",
                columns: new[] { "tenant_id", "file_base_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_summary_item_summary_id",
                schema: "cpe",
                table: "summary_item",
                column: "summary_id");

            migrationBuilder.CreateIndex(
                name: "IX_summary_item_tenant_id",
                schema: "cpe",
                table: "summary_item",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_summary_item_active",
                schema: "cpe",
                table: "summary_item",
                columns: new[] { "tenant_id", "electronic_document_id" },
                unique: true,
                filter: "released_at IS NULL");

            migrationBuilder.Sql(RlsSql.Enable("cpe", "summary_item"));

            // The default privileges of the first migration give the runtime role SELECT and INSERT on the new table; only releasing an item updates it.
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT SELECT, INSERT ON cpe.summary_item TO securefact_app;
                    GRANT UPDATE (released_at) ON cpe.summary_item TO securefact_app;
                  END IF;
                END
                $grant$;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION cpe.guard_electronic_document() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.state IN ('Accepted', 'AcceptedWithObservations', 'Rejected') THEN
                    RAISE EXCEPTION 'cpe.electronic_document: a document with a final SUNAT answer is immutable' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.signed_xml IS DISTINCT FROM OLD.signed_xml
                     OR NEW.digest_value IS DISTINCT FROM OLD.digest_value
                     OR NEW.document_id IS DISTINCT FROM OLD.document_id
                     OR NEW.company_id IS DISTINCT FROM OLD.company_id
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.issue_date IS DISTINCT FROM OLD.issue_date
                     OR NEW.document_type_code IS DISTINCT FROM OLD.document_type_code
                     OR NEW.file_base_name IS DISTINCT FROM OLD.file_base_name THEN
                    RAISE EXCEPTION 'cpe.electronic_document: the signed document is immutable' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE FUNCTION cpe.guard_summary_item() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.summary_id IS DISTINCT FROM OLD.summary_id
                     OR NEW.electronic_document_id IS DISTINCT FROM OLD.electronic_document_id
                     OR NEW.line_number IS DISTINCT FROM OLD.line_number
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR (OLD.released_at IS NOT NULL AND NEW.released_at IS DISTINCT FROM OLD.released_at) THEN
                    RAISE EXCEPTION 'cpe.summary_item: only an item that is still active can be released' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER summary_item_guard BEFORE UPDATE ON cpe.summary_item
                  FOR EACH ROW EXECUTE FUNCTION cpe.guard_summary_item();
                CREATE TRIGGER summary_item_no_delete BEFORE DELETE ON cpe.summary_item
                  FOR EACH ROW EXECUTE FUNCTION cpe.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "summary_item",
                schema: "cpe");

            migrationBuilder.DropIndex(
                name: "IX_electronic_document_tenant_id_file_base_name",
                schema: "cpe",
                table: "electronic_document");

            migrationBuilder.DropColumn(
                name: "issue_date",
                schema: "cpe",
                table: "electronic_document");
        }
    }
}
