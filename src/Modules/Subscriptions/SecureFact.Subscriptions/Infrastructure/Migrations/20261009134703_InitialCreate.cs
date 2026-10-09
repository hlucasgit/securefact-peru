using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SecureFact.Platform.Persistence;

#nullable disable

namespace SecureFact.Subscriptions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "subscription");

            migrationBuilder.CreateTable(
                name: "billing_policy",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    due_days = table.Column<int>(type: "integer", nullable: false),
                    suspend_after_days = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_policy", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "charge",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    plan_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    price_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monthly_fee = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    included_documents = table.Column<int>(type: "integer", nullable: true),
                    documents_issued = table.Column<int>(type: "integer", nullable: false),
                    overage_documents = table.Column<int>(type: "integer", nullable: false),
                    overage_unit_price = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    overage_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(7,4)", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    suspend_on = table.Column<DateOnly>(type: "date", nullable: true),
                    reseller_id = table.Column<Guid>(type: "uuid", nullable: true),
                    commission_schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charge", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commission_entry",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reseller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_period = table.Column<DateOnly>(type: "date", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    base_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_entry", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commission_schedule",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_schedule", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commission_settlement",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reseller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    entries = table.Column<int>(type: "integer", nullable: false),
                    settled_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_settlement", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commission_tier",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_accounts = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(5,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_tier", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    reverses_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plan_price",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    monthly_fee = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    included_documents = table.Column<int>(type: "integer", nullable: true),
                    overage_unit_price = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_price", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_policy_effective_from",
                schema: "subscription",
                table: "billing_policy",
                column: "effective_from",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_policy_version",
                schema: "subscription",
                table: "billing_policy",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_charge_due_on",
                schema: "subscription",
                table: "charge",
                column: "due_on");

            migrationBuilder.CreateIndex(
                name: "IX_charge_tenant_id",
                schema: "subscription",
                table: "charge",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_charge_tenant_id_period",
                schema: "subscription",
                table: "charge",
                columns: new[] { "tenant_id", "period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_entry_payment_id",
                schema: "subscription",
                table: "commission_entry",
                column: "payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_entry_reseller_id_month",
                schema: "subscription",
                table: "commission_entry",
                columns: new[] { "reseller_id", "month" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_schedule_effective_from",
                schema: "subscription",
                table: "commission_schedule",
                column: "effective_from",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_schedule_version",
                schema: "subscription",
                table: "commission_schedule",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_settlement_reseller_id_month",
                schema: "subscription",
                table: "commission_settlement",
                columns: new[] { "reseller_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_tier_schedule_id_min_accounts",
                schema: "subscription",
                table: "commission_tier",
                columns: new[] { "schedule_id", "min_accounts" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_charge_id",
                schema: "subscription",
                table: "payment",
                column: "charge_id");

            migrationBuilder.CreateIndex(
                name: "IX_payment_reverses_payment_id",
                schema: "subscription",
                table: "payment",
                column: "reverses_payment_id",
                unique: true,
                filter: "reverses_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payment_tenant_id",
                schema: "subscription",
                table: "payment",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_plan_price_plan_id_effective_from",
                schema: "subscription",
                table: "plan_price",
                columns: new[] { "plan_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plan_price_plan_id_version",
                schema: "subscription",
                table: "plan_price",
                columns: new[] { "plan_id", "version" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE subscription.plan_price ADD CONSTRAINT ck_plan_price
                  CHECK (monthly_fee >= 0 AND (included_documents IS NULL) = (overage_unit_price IS NULL) AND (overage_unit_price IS NULL OR overage_unit_price > 0) AND extract(day FROM effective_from) = 1);
                ALTER TABLE subscription.billing_policy ADD CONSTRAINT ck_billing_policy CHECK (due_days >= 0 AND (suspend_after_days IS NULL OR suspend_after_days >= 0));
                ALTER TABLE subscription.charge ADD CONSTRAINT ck_charge_amounts
                  CHECK (net_amount >= 0 AND tax_amount >= 0 AND total_amount = net_amount + tax_amount AND currency = 'PEN' AND extract(day FROM period) = 1);
                ALTER TABLE subscription.payment ADD CONSTRAINT ck_payment CHECK (amount <> 0 AND ((amount < 0) = (reverses_payment_id IS NOT NULL)));
                ALTER TABLE subscription.payment ADD CONSTRAINT fk_payment_charge FOREIGN KEY (charge_id) REFERENCES subscription.charge (id);
                ALTER TABLE subscription.payment ADD CONSTRAINT fk_payment_reverses FOREIGN KEY (reverses_payment_id) REFERENCES subscription.payment (id);
                ALTER TABLE subscription.commission_tier ADD CONSTRAINT ck_commission_tier CHECK (rate >= 0 AND rate <= 1 AND min_accounts >= 0);
                ALTER TABLE subscription.commission_tier ADD CONSTRAINT fk_commission_tier_schedule FOREIGN KEY (schedule_id) REFERENCES subscription.commission_schedule (id);
                """);

            // The first terms of the platform. They are data, versioned like any other: the next ones are published with a date and never rewrite these (ADR-062, ADR-063). Inserted before the row
            // level security is enabled, because the owner of the schema has no platform scope.
            migrationBuilder.Sql("""
                INSERT INTO subscription.billing_policy (id, version, effective_from, due_days, suspend_after_days, note, created_at)
                VALUES ('3f6c1d52-8a47-4b0e-9c25-6e1a0d7b4f31', 1, DATE '2026-01-01', 10, 15, 'Política inicial: vence a los 10 días y suspende a los 15 días de mora', now());
                INSERT INTO subscription.commission_schedule (id, version, effective_from, note, created_at)
                VALUES ('9a2e7b40-5d13-4c8f-b6a1-0f3d8e2c7a55', 1, DATE '2026-01-01', 'Términos iniciales: 20 %, 25 % desde 10 cuentas activas y 30 % desde 25', now());
                INSERT INTO subscription.commission_tier (id, schedule_id, min_accounts, rate) VALUES
                  ('b1d40c7e-2f98-4a63-8e15-7c0a9d3f6e01', '9a2e7b40-5d13-4c8f-b6a1-0f3d8e2c7a55', 0, 0.2000),
                  ('b1d40c7e-2f98-4a63-8e15-7c0a9d3f6e02', '9a2e7b40-5d13-4c8f-b6a1-0f3d8e2c7a55', 10, 0.2500),
                  ('b1d40c7e-2f98-4a63-8e15-7c0a9d3f6e03', '9a2e7b40-5d13-4c8f-b6a1-0f3d8e2c7a55', 25, 0.3000);
                """);

            migrationBuilder.Sql(RlsSql.EnableGlobalReference("subscription", "plan_price"));
            migrationBuilder.Sql(RlsSql.EnableGlobalReference("subscription", "billing_policy"));
            migrationBuilder.Sql(RlsSql.Enable("subscription", "charge", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.Enable("subscription", "payment", RlsMode.TenantOrPlatform));
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("subscription", "commission_schedule"));
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("subscription", "commission_tier"));
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("subscription", "commission_entry"));
            migrationBuilder.Sql(RlsSql.EnablePlatformOnly("subscription", "commission_settlement"));
            migrationBuilder.Sql(RlsSql.GrantToAppRole("subscription", "SELECT, INSERT"));
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT UPDATE ON subscription.charge TO securefact_app;
                  END IF;
                END
                $grant$;
                """);

            // What was published, paid or earned is never edited or deleted, even by the owner of the application code. A charge changes in one way only: it can be voided, once, and nothing else about it moves.
            migrationBuilder.Sql("""
                CREATE FUNCTION subscription.forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION '% is not allowed on %.%', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
                END
                $fn$;

                CREATE FUNCTION subscription.guard_charge() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'subscription.charge: a charge is never deleted' USING ERRCODE = '42501';
                  END IF;
                  IF OLD.voided_at IS NOT NULL THEN
                    RAISE EXCEPTION 'subscription.charge: a void charge is immutable' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.period IS DISTINCT FROM OLD.period
                     OR NEW.monthly_fee IS DISTINCT FROM OLD.monthly_fee
                     OR NEW.overage_amount IS DISTINCT FROM OLD.overage_amount
                     OR NEW.net_amount IS DISTINCT FROM OLD.net_amount
                     OR NEW.tax_amount IS DISTINCT FROM OLD.tax_amount
                     OR NEW.total_amount IS DISTINCT FROM OLD.total_amount
                     OR NEW.issued_on IS DISTINCT FROM OLD.issued_on
                     OR NEW.due_on IS DISTINCT FROM OLD.due_on
                     OR NEW.suspend_on IS DISTINCT FROM OLD.suspend_on
                     OR NEW.reseller_id IS DISTINCT FROM OLD.reseller_id
                     OR NEW.commission_schedule_id IS DISTINCT FROM OLD.commission_schedule_id THEN
                    RAISE EXCEPTION 'subscription.charge: the figures of a charge are immutable' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER trg_charge_guard BEFORE UPDATE OR DELETE ON subscription.charge FOR EACH ROW EXECUTE FUNCTION subscription.guard_charge();
                CREATE TRIGGER trg_plan_price_immutable BEFORE UPDATE OR DELETE ON subscription.plan_price FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                CREATE TRIGGER trg_billing_policy_immutable BEFORE UPDATE OR DELETE ON subscription.billing_policy FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                CREATE TRIGGER trg_payment_immutable BEFORE UPDATE OR DELETE ON subscription.payment FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                CREATE TRIGGER trg_commission_schedule_immutable BEFORE UPDATE OR DELETE ON subscription.commission_schedule FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                CREATE TRIGGER trg_commission_tier_immutable BEFORE UPDATE OR DELETE ON subscription.commission_tier FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                CREATE TRIGGER trg_commission_entry_immutable BEFORE UPDATE OR DELETE ON subscription.commission_entry FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                CREATE TRIGGER trg_commission_settlement_immutable BEFORE UPDATE OR DELETE ON subscription.commission_settlement FOR EACH ROW EXECUTE FUNCTION subscription.forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_policy",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "charge",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "commission_entry",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "commission_schedule",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "commission_settlement",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "commission_tier",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "payment",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "plan_price",
                schema: "subscription");
        }
    }
}
