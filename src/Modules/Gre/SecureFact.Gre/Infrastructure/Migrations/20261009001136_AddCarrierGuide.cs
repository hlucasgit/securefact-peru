using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Gre.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierGuide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "document_type_code",
                schema: "gre",
                table: "series",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "09");

            migrationBuilder.AlterColumn<string>(
                name: "motive_code",
                schema: "gre",
                table: "guide",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2);

            migrationBuilder.AlterColumn<string>(
                name: "modality_code",
                schema: "gre",
                table: "guide",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2);

            migrationBuilder.AddColumn<string>(
                name: "document_type_code",
                schema: "gre",
                table: "guide",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "09");

            // The type of a series follows its first letter (T sender, V carrier), and the type of a guide never changes once it is written.
            migrationBuilder.Sql("""
                ALTER TABLE gre.series ADD CONSTRAINT ck_series_type_prefix
                  CHECK ((document_type_code = '09' AND code LIKE 'T%') OR (document_type_code = '31' AND code LIKE 'V%'));

                CREATE OR REPLACE FUNCTION gre.guard_guide() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.state IN ('Accepted', 'AcceptedWithObservations', 'Rejected', 'Failed') THEN
                    RAISE EXCEPTION 'gre.guide: a guide with a final answer is immutable' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.signed_xml IS DISTINCT FROM OLD.signed_xml
                     OR NEW.digest_value IS DISTINCT FROM OLD.digest_value
                     OR NEW.request IS DISTINCT FROM OLD.request
                     OR NEW.document_type_code IS DISTINCT FROM OLD.document_type_code
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

                CREATE OR REPLACE FUNCTION gre.guard_series() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.last_number < OLD.last_number OR NEW.code IS DISTINCT FROM OLD.code OR NEW.company_id IS DISTINCT FROM OLD.company_id
                     OR NEW.document_type_code IS DISTINCT FROM OLD.document_type_code THEN
                    RAISE EXCEPTION 'gre.series: the numbering only moves forward' USING ERRCODE = '42501';
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
                name: "document_type_code",
                schema: "gre",
                table: "series");

            migrationBuilder.DropColumn(
                name: "document_type_code",
                schema: "gre",
                table: "guide");

            migrationBuilder.AlterColumn<string>(
                name: "motive_code",
                schema: "gre",
                table: "guide",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "modality_code",
                schema: "gre",
                table: "guide",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2,
                oldNullable: true);
        }
    }
}
