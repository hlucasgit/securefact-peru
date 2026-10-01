using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.CpeEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVoidReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "cpe",
                table: "summary_item",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
            // The reason for a void is part of the record: it can never change once the item exists.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION cpe.guard_summary_item() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.summary_id IS DISTINCT FROM OLD.summary_id
                     OR NEW.electronic_document_id IS DISTINCT FROM OLD.electronic_document_id
                     OR NEW.line_number IS DISTINCT FROM OLD.line_number
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.reason IS DISTINCT FROM OLD.reason
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
            migrationBuilder.DropColumn(
                name: "reason",
                schema: "cpe",
                table: "summary_item");
        }
    }
}
