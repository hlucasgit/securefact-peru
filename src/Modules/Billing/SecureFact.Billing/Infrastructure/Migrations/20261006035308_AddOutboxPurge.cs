using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFact.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The outbox stays append-only for everybody, the schema owner included, with one exception: a delivered message older than a day may be deleted by billing.purge_outbox_messages,
            // which marks its own transaction for the guard. Pending and dead messages can never be deleted.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION billing.guard_outbox_message() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    IF current_setting('app.outbox_purge', true) IS DISTINCT FROM 'on'
                       OR OLD.processed_at IS NULL
                       OR OLD.processed_at > now() - interval '1 day' THEN
                      RAISE EXCEPTION 'billing.outbox_message is append-only (only delivered messages older than a day are purged, through billing.purge_outbox_messages)' USING ERRCODE = '42501';
                    END IF;
                    RETURN OLD;
                  END IF;
                  IF NEW.id IS DISTINCT FROM OLD.id
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.event_type IS DISTINCT FROM OLD.event_type
                     OR NEW.payload IS DISTINCT FROM OLD.payload
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'billing.outbox_message: the event is immutable; only delivery bookkeeping changes' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """);

            // Runs as the schema owner so that the runtime role needs no DELETE privilege; it spans tenants, so it sets the platform scope for its own transaction only.
            migrationBuilder.Sql("""
                CREATE FUNCTION billing.purge_outbox_messages(retention interval) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                DECLARE
                  removed integer;
                BEGIN
                  IF retention < interval '1 day' THEN
                    RAISE EXCEPTION 'The retention of the outbox must be at least one day.' USING ERRCODE = '22023';
                  END IF;
                  PERFORM set_config('app.outbox_purge', 'on', true);
                  PERFORM set_config('app.scope', 'platform', true);
                  DELETE FROM billing.outbox_message WHERE processed_at IS NOT NULL AND processed_at < now() - retention;
                  GET DIAGNOSTICS removed = ROW_COUNT;
                  RETURN removed;
                END
                $fn$;

                REVOKE ALL ON FUNCTION billing.purge_outbox_messages(interval) FROM PUBLIC;
                """);

            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                    GRANT EXECUTE ON FUNCTION billing.purge_outbox_messages(interval) TO securefact_app;
                  END IF;
                END
                $grant$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION billing.purge_outbox_messages(interval);");
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION billing.guard_outbox_message() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'billing.outbox_message is append-only (DELETE is not allowed)' USING ERRCODE = '42501';
                  END IF;
                  IF NEW.id IS DISTINCT FROM OLD.id
                     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.event_type IS DISTINCT FROM OLD.event_type
                     OR NEW.payload IS DISTINCT FROM OLD.payload
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'billing.outbox_message: the event is immutable; only delivery bookkeeping changes' USING ERRCODE = '42501';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """);
        }
    }
}
