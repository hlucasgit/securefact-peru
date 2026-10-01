using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.CpeEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reference_document_id",
                schema: "cpe",
                table: "electronic_document",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reference_type_code",
                schema: "cpe",
                table: "electronic_document",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);
            // The reference of a note is part of what was signed: it can never change either.
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
                     OR NEW.reference_document_id IS DISTINCT FROM OLD.reference_document_id
                     OR NEW.reference_type_code IS DISTINCT FROM OLD.reference_type_code
                     OR NEW.file_base_name IS DISTINCT FROM OLD.file_base_name THEN
                    RAISE EXCEPTION 'cpe.electronic_document: the signed document is immutable' USING ERRCODE = '42501';
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
                name: "reference_document_id",
                schema: "cpe",
                table: "electronic_document");

            migrationBuilder.DropColumn(
                name: "reference_type_code",
                schema: "cpe",
                table: "electronic_document");
        }
    }
}
