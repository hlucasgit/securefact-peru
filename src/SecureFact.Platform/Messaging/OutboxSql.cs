namespace SecureFact.Platform.Messaging;

/// <summary>
/// The database side of the outbox of a module (ADR-022, ADR-035): who may do what to <c>{schema}.outbox_message</c>. A module's migration creates the table and calls these.
/// </summary>
public static class OutboxSql
{
    /// <summary>
    /// Grants, the guard trigger and the purge function. The runtime role inserts events and updates only the delivery bookkeeping, never deletes; a trigger keeps the event itself immutable
    /// and refuses every delete except the one made by <c>purge_outbox_messages</c> on a message delivered more than a day ago.
    /// </summary>
    public static string Secure(string schema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]*$"))
        {
            throw new ArgumentException("The schema must be a lower-case identifier.", nameof(schema));
        }

        return $$"""
            DO $grant$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                GRANT SELECT, INSERT ON {{schema}}.outbox_message TO securefact_app;
                GRANT UPDATE (attempts, next_attempt_at, locked_until, processed_at, dead_at, last_error) ON {{schema}}.outbox_message TO securefact_app;
              END IF;
            END
            $grant$;

            CREATE FUNCTION {{schema}}.guard_outbox_message() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
              IF TG_OP = 'DELETE' THEN
                IF current_setting('app.outbox_purge', true) IS DISTINCT FROM 'on'
                   OR OLD.processed_at IS NULL
                   OR OLD.processed_at > now() - interval '1 day' THEN
                  RAISE EXCEPTION '{{schema}}.outbox_message is append-only (only delivered messages older than a day are purged, through {{schema}}.purge_outbox_messages)' USING ERRCODE = '42501';
                END IF;
                RETURN OLD;
              END IF;
              IF NEW.id IS DISTINCT FROM OLD.id
                 OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                 OR NEW.event_type IS DISTINCT FROM OLD.event_type
                 OR NEW.payload IS DISTINCT FROM OLD.payload
                 OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                RAISE EXCEPTION '{{schema}}.outbox_message: the event is immutable; only delivery bookkeeping changes' USING ERRCODE = '42501';
              END IF;
              RETURN NEW;
            END
            $fn$;

            CREATE TRIGGER outbox_message_guard BEFORE UPDATE OR DELETE ON {{schema}}.outbox_message
              FOR EACH ROW EXECUTE FUNCTION {{schema}}.guard_outbox_message();

            CREATE FUNCTION {{schema}}.purge_outbox_messages(retention interval) RETURNS integer
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              removed integer;
            BEGIN
              IF retention < interval '1 day' THEN
                RAISE EXCEPTION 'The retention of the outbox must be at least one day.' USING ERRCODE = '22023';
              END IF;
              PERFORM set_config('app.outbox_purge', 'on', true);
              PERFORM set_config('app.scope', 'platform', true);
              DELETE FROM {{schema}}.outbox_message WHERE processed_at IS NOT NULL AND processed_at < now() - retention;
              GET DIAGNOSTICS removed = ROW_COUNT;
              RETURN removed;
            END
            $fn$;

            REVOKE ALL ON FUNCTION {{schema}}.purge_outbox_messages(interval) FROM PUBLIC;

            DO $grant$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'securefact_app') THEN
                GRANT EXECUTE ON FUNCTION {{schema}}.purge_outbox_messages(interval) TO securefact_app;
              END IF;
            END
            $grant$;
            """;
    }
}
