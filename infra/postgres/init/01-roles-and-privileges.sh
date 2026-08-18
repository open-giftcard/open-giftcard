#!/bin/bash
# Creates the two PostgreSQL roles required by ADR-019 and fixes their
# privileges. Runs once, on first container start, before any migration.
#
#   migration owner  - owns schemas and tables, creates them, not used at runtime
#   runtime app role - non-superuser, NOBYPASSRLS, owns nothing, DML only
#
# Schemas are created here (rather than only by EF migrations) so that
# ALTER DEFAULT PRIVILEGES can be attached to them up front. Every table the
# migrator later creates then inherits the correct grants automatically, which
# is what keeps the audit schema append-only for the runtime role.

set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    -- ---------------------------------------------------------------- roles
    CREATE ROLE "${GIFTCARD_MIGRATOR_USER}"
        LOGIN PASSWORD '${GIFTCARD_MIGRATOR_PASSWORD}'
        NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

    CREATE ROLE "${GIFTCARD_APP_USER}"
        LOGIN PASSWORD '${GIFTCARD_APP_PASSWORD}'
        NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

    GRANT CONNECT ON DATABASE "${POSTGRES_DB}" TO "${GIFTCARD_MIGRATOR_USER}";
    GRANT CONNECT ON DATABASE "${POSTGRES_DB}" TO "${GIFTCARD_APP_USER}";

    -- The ltree extension backs the organization hierarchy path (ADR-010).
    -- Created here because installing an extension requires elevated rights.
    CREATE EXTENSION IF NOT EXISTS ltree;

    -- ------------------------------------------------------- module schemas
    CREATE SCHEMA IF NOT EXISTS organizations AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS audit         AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS identity      AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS "authorization" AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS ledger        AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS corporate_credits AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS gift_cards     AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS distribution   AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS sharing        AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS payments       AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS notifications  AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";
    CREATE SCHEMA IF NOT EXISTS partners      AUTHORIZATION "${GIFTCARD_MIGRATOR_USER}";

    -- The runtime role may use the schemas but never create objects in them.
    GRANT USAGE ON SCHEMA organizations   TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA audit           TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA identity        TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA "authorization" TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA ledger          TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA corporate_credits TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA gift_cards       TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA distribution     TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA sharing          TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA payments         TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA notifications     TO "${GIFTCARD_APP_USER}";
    GRANT USAGE ON SCHEMA partners         TO "${GIFTCARD_APP_USER}";

    REVOKE CREATE ON SCHEMA organizations   FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA audit           FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA identity        FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA "authorization" FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA ledger          FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA corporate_credits FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA gift_cards       FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA distribution     FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA sharing          FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA payments         FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA notifications     FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA partners         FROM "${GIFTCARD_APP_USER}";
    REVOKE CREATE ON SCHEMA public          FROM PUBLIC;

    -- -------------------------------------------------- default privileges
    -- Organizations: ordinary business table privileges.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA organizations
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Authorization: ordinary business table privileges. The global permission
    -- catalogue is seeded by the migrator, so the runtime role needs no more
    -- than DML on it.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA "authorization"
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Identity: users, sessions, and hashed refresh credentials.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA identity
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Audit: append-only. SELECT and INSERT only â€” deliberately no UPDATE and
    -- no DELETE, so committed audit rows cannot be altered by the application
    -- even if application code tried (ADR-008, ADR-019).
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA audit
        GRANT SELECT, INSERT ON TABLES TO "${GIFTCARD_APP_USER}";
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA audit
        GRANT USAGE, SELECT ON SEQUENCES TO "${GIFTCARD_APP_USER}";

    -- Financial history is immutable for the runtime role. Accounts,
    -- transactions, entries, and allocations are inserted and read only.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA ledger
        GRANT SELECT, INSERT ON TABLES TO "${GIFTCARD_APP_USER}";
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA corporate_credits
        GRANT SELECT, INSERT ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Gift-card identity and provenance are retained; lifecycle/ownership
    -- transitions update the current card row while history stays append-only.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA gift_cards
        GRANT SELECT, INSERT, UPDATE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Invitation state is mutable; distribution events remain append-only
    -- through explicit revokes in the Distribution migration.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA distribution
        GRANT SELECT, INSERT, UPDATE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Share lifecycle rows are mutable through constrained transitions; events
    -- remain append-only through their database trigger and lack DELETE grants.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA sharing
        GRANT SELECT, INSERT, UPDATE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Payment credentials are inserted, read, and marked consumed exactly once.
    -- No DELETE: a spent or expired token is evidence and must remain readable
    -- for reconciliation and dispute handling (ADR-017, ADR-018).
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA payments
        GRANT SELECT, INSERT, UPDATE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- Partner registry: reseller records and their hashed API-client secrets.
    -- Rows are inserted, read, and updated for rotation and the kill switch.
    -- No DELETE: a retired credential is evidence, and a partner row anchors
    -- the funding tenant of every card it ever minted.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA partners
        GRANT SELECT, INSERT, UPDATE ON TABLES TO "${GIFTCARD_APP_USER}";

    -- The dispatcher updates state and clears credential columns; no delete,
    -- because a settled message is operational evidence.
    ALTER DEFAULT PRIVILEGES FOR ROLE "${GIFTCARD_MIGRATOR_USER}" IN SCHEMA notifications
        GRANT SELECT, INSERT, UPDATE ON TABLES TO "${GIFTCARD_APP_USER}";
EOSQL

echo "giftcard: migration-owner and runtime roles created; audit schema is append-only for the runtime role."
