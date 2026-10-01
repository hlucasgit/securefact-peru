using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.CpeEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLineStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_summary_item_active",
                schema: "cpe",
                table: "summary_item");

            migrationBuilder.AddColumn<int>(
                name: "line_status",
                schema: "cpe",
                table: "summary_item",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ux_summary_item_active",
                schema: "cpe",
                table: "summary_item",
                columns: new[] { "tenant_id", "electronic_document_id", "line_status" },
                unique: true,
                filter: "released_at IS NULL");
            // The kind of an item (reports or voids) is part of the record: it can never change once the item exists.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION cpe.guard_summary_item() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.summary_id IS DISTINCT FROM OLD.summary_id
                     OR NEW.electronic_document_id IS DISTINCT FROM OLD.electronic_document_id
                     OR NEW.line_number IS DISTINCT FROM OLD.line_number
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.reason IS DISTINCT FROM OLD.reason
                     OR NEW.line_status IS DISTINCT FROM OLD.line_status
                     OR (OLD.released_at IS NOT NULL AND NEW.released_at IS DISTINCT FROM OLD.released_at) THEN
                    RAISE EXCEPTION 'cpe.summary_item: only an item that is still active can be released' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_summary_item_active",
                schema: "cpe",
                table: "summary_item");

            migrationBuilder.DropColumn(
                name: "line_status",
                schema: "cpe",
                table: "summary_item");

            migrationBuilder.CreateIndex(
                name: "ux_summary_item_active",
                schema: "cpe",
                table: "summary_item",
                columns: new[] { "tenant_id", "electronic_document_id" },
                unique: true,
                filter: "released_at IS NULL");
        }
    }
}
