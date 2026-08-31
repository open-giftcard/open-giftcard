#!/bin/bash
# Creates the portal and cardholder session databases and their split roles.
#
# These are deliberately separate databases from the platform's. The portal and
# the cardholder each own a small store used only for browser sessions and
# activation context, and neither may reach the platform database or each
# other's (ADR-CARD-008).
#
# Unlike infra/postgres/init, this does not run from the PostgreSQL entrypoint.
# The entrypoint only runs its init directory the first time a data volume is
# created, so a stack that was first brought up with the base compose file
# would never get these databases. This runs as its own short-lived service in
# docker-compose.full.yml instead, and is safe to re-run.

set -euo pipefail

: "${POSTGRES_SUPERUSER:?set POSTGRES_SUPERUSER}"
: "${PGPASSWORD:?set PGPASSWORD to the superuser password}"
: "${PORTAL_DB:?set PORTAL_DB}"
: "${PORTAL_APP_USER:?set PORTAL_APP_USER}"
: "${PORTAL_APP_PASSWORD:?set PORTAL_APP_PASSWORD}"
: "${PORTAL_MIGRATOR_USER:?set PORTAL_MIGRATOR_USER}"
: "${PORTAL_MIGRATOR_PASSWORD:?set PORTAL_MIGRATOR_PASSWORD}"
: "${CARDHOLDER_DB:?set CARDHOLDER_DB}"
: "${CARDHOLDER_APP_USER:?set CARDHOLDER_APP_USER}"
: "${CARDHOLDER_APP_PASSWORD:?set CARDHOLDER_APP_PASSWORD}"
: "${CARDHOLDER_MIGRATOR_USER:?set CARDHOLDER_MIGRATOR_USER}"
: "${CARDHOLDER_MIGRATOR_PASSWORD:?set CARDHOLDER_MIGRATOR_PASSWORD}"

HOST="${POSTGRES_HOST:-postgres}"

run_sql() {
    psql -v ON_ERROR_STOP=1 --host "$HOST" --username "$POSTGRES_SUPERUSER" --dbname "$1" --command "$2"
}

# CREATE ROLE and CREATE DATABASE have no IF NOT EXISTS, so existence is
# checked first. This whole script must be safe to run against a stack that
# already has them.
ensure_role() {
    local role="$1" password="$2"
    if [ "$(psql -tAX --host "$HOST" --username "$POSTGRES_SUPERUSER" --dbname postgres \
        --command "select 1 from pg_roles where rolname = '${role}'")" = "1" ]; then
        echo "role ${role} already exists"
    else
        run_sql postgres "CREATE ROLE \"${role}\" LOGIN PASSWORD '${password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS"
        echo "created role ${role}"
    fi
}

ensure_database() {
    local database="$1" owner="$2"
    if [ "$(psql -tAX --host "$HOST" --username "$POSTGRES_SUPERUSER" --dbname postgres \
        --command "select 1 from pg_database where datname = '${database}'")" = "1" ]; then
        echo "database ${database} already exists"
    else
        run_sql postgres "CREATE DATABASE \"${database}\" OWNER \"${owner}\""
        echo "created database ${database}"
    fi
}

grant_runtime() {
    local database="$1" migrator="$2" app="$3"
    run_sql "$database" "GRANT CONNECT ON DATABASE \"${database}\" TO \"${app}\""
    run_sql "$database" "GRANT USAGE ON SCHEMA public TO \"${app}\""
    run_sql "$database" "REVOKE CREATE ON SCHEMA public FROM \"${app}\""
    # Attached before the migrator creates anything, so every table it later
    # creates inherits these grants. The runtime role owns nothing and holds no
    # DDL privilege, the same split the platform database uses (ADR-019).
    run_sql "$database" "ALTER DEFAULT PRIVILEGES FOR ROLE \"${migrator}\" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO \"${app}\""
    run_sql "$database" "ALTER DEFAULT PRIVILEGES FOR ROLE \"${migrator}\" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO \"${app}\""
    # Covers a re-run against a database whose tables already exist.
    run_sql "$database" "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO \"${app}\""
    run_sql "$database" "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO \"${app}\""
}

ensure_role "$PORTAL_MIGRATOR_USER" "$PORTAL_MIGRATOR_PASSWORD"
ensure_role "$PORTAL_APP_USER" "$PORTAL_APP_PASSWORD"
ensure_database "$PORTAL_DB" "$PORTAL_MIGRATOR_USER"
grant_runtime "$PORTAL_DB" "$PORTAL_MIGRATOR_USER" "$PORTAL_APP_USER"

ensure_role "$CARDHOLDER_MIGRATOR_USER" "$CARDHOLDER_MIGRATOR_PASSWORD"
ensure_role "$CARDHOLDER_APP_USER" "$CARDHOLDER_APP_PASSWORD"
ensure_database "$CARDHOLDER_DB" "$CARDHOLDER_MIGRATOR_USER"
grant_runtime "$CARDHOLDER_DB" "$CARDHOLDER_MIGRATOR_USER" "$CARDHOLDER_APP_USER"

echo "client databases ready"
