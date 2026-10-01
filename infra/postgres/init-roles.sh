#!/bin/sh
# Creates the runtime application role. It is NOT the owner of tables and has no BYPASSRLS,
# so Row Level Security policies always apply to it (ADR-003).
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${SF_APP_DB_USER}') THEN
    CREATE ROLE ${SF_APP_DB_USER} LOGIN PASSWORD '${SF_APP_DB_PASSWORD}' NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
END
\$\$;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO ${SF_APP_DB_USER};
SQL
