# Security Policy

## Reporting a vulnerability

Use GitHub's private vulnerability reporting on this repository: **Security →
Report a vulnerability**. That opens a private advisory visible only to the
maintainers. Please do not open a public issue for anything you believe is
exploitable.

Include what you did, what happened, and what you expected. A failing request or
a short reproduction against a local `docker compose up` is worth more than a
scanner report.

There is no bounty and no formal response-time commitment. This is a small
project maintained by one person.

## Supported versions

`v0.9.1` is the current release and the only supported version. `main` is
where fixes land, and a fix reaches you in the next tag rather than by
backport. Local tags predating the open-source cleanup were never published and
should not be used.

## What the platform actually enforces

Stated so you know where to look, and so nothing here overstates the
implementation.

**Tenant isolation.** PostgreSQL Row-Level Security is the authoritative
barrier, not application filtering. Tenant tables carry a forced RLS policy from
their first migration, and the runtime database role is non-superuser and
`NOBYPASSRLS`. Scope comes from server-verified state, never from a
client-supplied organization or membership id. A refusal returns 404 rather than
a confirming 403, so a probe cannot learn that another tenant's data exists.

*Which tables, exactly.* Every table that holds tenant-owned data has RLS
enabled and forced. Some tables deliberately do not, and they are listed here
rather than left for a reviewer to find by querying `pg_class`:

- `organizations.organizations` is `ENABLE` but not `FORCE`. Forcing it would
  subject the table owner to the policy, and the policy depends on a
  `SECURITY DEFINER` function that reads this very table to resolve the
  caller's tenant root. The owner is the migration role, which by ADR-019 is
  never used at runtime; the runtime role owns nothing and stays subject to the
  policy.
- `identity.users`, `identity.sessions` and `identity.refresh_tokens` hold
  **global** identities. One person may hold memberships in several tenants, so
  there is no single tenant these rows belong to and no column to isolate on.
  Access is constrained in the application, and a session carries no authority
  until a membership is resolved for the organization being acted in.
- `payments.pos_clients` and `payments.pos_terminals` are a platform-wide
  device registry with no tenant column. A POS device carries no tenant of its
  own by design; the tenant of a sale comes from the card the presented
  credential resolves to. Registering and retiring devices requires the
  platform permission `platform.pos.clients.manage`, not a tenant role.
- The `authorization` platform catalogue and bootstrap tables, and the
  `audit` checkpoint, seal and witness tables, are platform-scoped rather than
  tenant-scoped. `audit.audit_records` itself, which does hold tenant data, is
  enabled and forced.
- `__ef_migrations_history` in each schema is owned by the migration role.

`Test-RowLevelSecurityPosture` in the integration suite asserts this exact list,
so a new tenant table cannot be added without either a forced policy or a
deliberate, reviewed entry here.

**Two database roles.** Migrations run as an owner role; the application runs as
a role that owns nothing and holds no DDL privilege, so a compromised
application cannot alter its own schema.

**Append-only audit, enforced by privilege.** The runtime role has `SELECT` and
`INSERT` on the audit schema and nothing else. An audited administrative change
and its audit record commit atomically; denials are recorded on an independent
connection so they survive the rollback.

**Money.** Every value change is an immutable, balanced, double-entry ledger
posting. No mutable balance column is authoritative anywhere. Financial
operations are idempotent under `operation_type + idempotency_key` with a
database unique constraint, and value-changing work runs at `SERIALIZABLE`.

**Credentials.** Only hashes persist. Raw secrets are returned once, under
`no-store`, and never again. Claim, share, e-pin, and payment credentials all
use a 256-bit random secret with only its SHA-256 stored and a constant-time
comparison; a parse failure clears the identifier so an attacker-chosen value
cannot reach an RLS session variable. Share PINs are PBKDF2 at 210,000
iterations. Unknown, expired, consumed, and replayed credentials are refused
identically so the response is not an oracle.

POS device tokens carry identity only. The active client and terminal are
re-resolved from PostgreSQL on every request, so permanently disabling either
one invalidates already-issued device tokens immediately. Client secret rotation
uses replacement registration followed by audited retirement; no raw secret is
stored for recovery or in-place replay.

**Sessions.** 15-minute access tokens and rotating 30-day refresh tokens with
reuse detection: presenting a consumed token revokes the session and its whole
token family, and records an audit event. Those lifetimes are fixed and startup
fails if configuration changes them.

**Transport and surface.** The API is bearer-only and enables no CORS policy;
browsers reach it through a same-origin BFF that keeps refresh tokens
server-side. `X-Forwarded-For` is trusted for exactly one hop and only from
literal allowlisted addresses, and is ignored entirely when none are configured.
`/swagger`, `/demo`, and the demonstration seed exist only in Development.

## Known gaps

These are real and deliberate. The project has never been deployed, and nothing
here should be read as a production-readiness claim.

- **Audit checkpoint signing has no managed key custody.** The signing and
  witness mechanism exists; the KMS/HSM signer and WORM storage adapters do not.
  Checkpointing therefore refuses to start outside Development rather than
  falling back to a local key. This is the largest gap between the audit model
  as described and a deployment you could rely on.
- **Ingress flood control remains operator-owned.** Authenticated partner mint
  velocity is enforced in PostgreSQL and stays exact across API replicas. The
  public ingress must still apply a coarse source-IP limit before authentication
  to protect connection and CPU capacity. The prepaid float and per-order ceiling
  remain the authoritative financial bounds.
- **No SMS provider.** Outside Development, phone distribution, direct sharing,
  and bulk acceptance are rejected before commit. An operator must add and
  certify an SMS adapter before enabling those journeys.
- **The POS till is a reference client, not retail software.** It now carries
  response security headers, a pinned API contract, readiness, and durable key
  configuration. The non-Docker smoke gate proves a live backend payment and
  full refund; physical device and human browser certification remain.
- **No staging certification, no threat model document, no penetration test.**

## Scope

In scope: anything in this repository, including the demonstration seed and the
Development-only endpoints, if it can be reached outside Development.

Out of scope: findings that depend on the deliberate gaps listed above,
misconfiguration of a deployment you control, and the placeholder credentials in
`.env.example`, which exist to be replaced.
