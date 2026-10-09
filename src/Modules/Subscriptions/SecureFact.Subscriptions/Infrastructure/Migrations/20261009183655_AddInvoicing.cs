using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Subscriptions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_profile",
                schema: "subscription",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    document_number = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_profile", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "charge_document",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issuer_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    series = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charge_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoicing_settings",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    issuer_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_note_series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_note_series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoicing_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_profile_tenant_id",
                schema: "subscription",
                table: "billing_profile",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_charge_document_charge_id_kind",
                schema: "subscription",
                table: "charge_document",
                columns: new[] { "charge_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_charge_document_tenant_id",
                schema: "subscription",
                table: "charge_document",
                column: "tenant_id");

            migrationBuilder.Sql("""
                ALTER TABLE subscription.billing_profile ADD CONSTRAINT ck_billing_profile_type CHECK (document_type_code IN ('1', '6'));
                ALTER TABLE subscription.charge_document ADD CONSTRAINT fk_charge_document_charge FOREIGN KEY (charge_id) REFERENCES subscription.charge (id);
                ALTER TABLE subscription.charge_document ADD CONSTRAINT ck_charge_document_kind CHECK (kind IN ('Invoice', 'CreditNote'));
                ALTER TABLE subscription.invoicing_settings ADD CONSTRAINT ck_invoicing_settings_single CHECK (id = 1);
                """);

            // Who the platform invoices as and with which account is read and written by the tenant itself (its own profile) or by the platform; the link to the issued documents is only added.
            migrationBuilder.Sql(RlsSql.Enable("subscription", "billing_profile", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.Enable("subscription", "charge_document", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("subscription", "invoicing_settings"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("subscription", "SELECT, INSERT"));
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT UPDATE ON subscription.billing_profile, subscription.invoicing_settings TO securefact_app;
                  END IF;
                END
                $grant$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_charge_document_immutable BEFORE UPDATE OR DELETE ON subscription.charge_document FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_profile",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "charge_document",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "invoicing_settings",
                schema: "subscription");
        }
    }
}
