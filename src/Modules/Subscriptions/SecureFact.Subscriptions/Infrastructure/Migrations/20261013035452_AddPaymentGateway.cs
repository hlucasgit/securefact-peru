using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Subscriptions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_settings",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    public_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    protected_secret_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempt",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    gateway_charge_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempt", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_charge_id_created_at",
                schema: "subscription",
                table: "payment_attempt",
                columns: new[] { "charge_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_state",
                schema: "subscription",
                table: "payment_attempt",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_tenant_id",
                schema: "subscription",
                table: "payment_attempt",
                column: "tenant_id");

            migrationBuilder.Sql("""
                ALTER TABLE subscription.gateway_settings ADD CONSTRAINT ck_gateway_settings_single CHECK (id = 1);
                ALTER TABLE subscription.gateway_settings ADD CONSTRAINT ck_gateway_settings_provider CHECK (provider IN ('Culqi', 'Sandbox'));
                ALTER TABLE subscription.payment_attempt ADD CONSTRAINT fk_payment_attempt_charge FOREIGN KEY (charge_id) REFERENCES subscription.charge (id);
                ALTER TABLE subscription.payment_attempt ADD CONSTRAINT ck_payment_attempt_amount CHECK (amount > 0);
                ALTER TABLE subscription.payment_attempt ADD CONSTRAINT ck_payment_attempt_state CHECK (state IN ('Started', 'Paid', 'Declined', 'NeedsAuthentication', 'Failed', 'UnderReview', 'Resolved'));
                """);

            // The keys of the gateway are the platform's alone; the attempts are the tenant's to read (and the platform's to review). An attempt is never deleted: the runtime role has no DELETE and a trigger refuses it for anyone.
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("subscription", "gateway_settings"));
            migrationBuilder.Sql(RlsSql.Enable("subscription", "payment_attempt", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("subscription", "SELECT, INSERT"));
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT UPDATE ON subscription.gateway_settings, subscription.payment_attempt TO securefact_app;
                  END IF;
                END
                $grant$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_payment_attempt_no_delete BEFORE DELETE ON subscription.payment_attempt FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_settings",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "payment_attempt",
                schema: "subscription");
        }
    }
}
