# Capability Matrix

What this platform does, how far each capability is actually taken, and which
parts stay an operator's responsibility whatever the version number says.

`README.md` says what works and what is not done. `RELEASE_READINESS.md` gates
the deployment certified label. This document sits between them and answers a
different question: if you adopted this, what would you be getting, and what
would still be yours to build?

## How to read the states

| State | Meaning |
| --- | --- |
| **Implemented** | Built, covered by tests, and enforced by the platform. Safe to depend on within the promises `VERSIONING.md` makes. |
| **Reference implementation** | Built and working, but shipped as a demonstration of the contract rather than as a finished product. Expect to replace or extend it. |
| **Development only** | Exists, and deliberately refuses to run outside Development. |
| **Operator responsibility** | The platform provides the hook, the contract, or the refusal. Supplying the thing itself is yours. |
| **Planned** | Named as intended work with no implementation. |
| **Unsupported** | Deliberately absent. Not on the roadmap for the current line. |

Nothing here is marked Implemented on the strength of its code existing. The
distinction that matters is whether the platform enforces it, and where a
guarantee rests on a database constraint or privilege rather than on application
code, that is said explicitly.

## Money and accounting

| Capability | State | Notes |
| --- | --- | --- |
| Double-entry ledger | Implemented | Balance is enforced twice: in the domain, and by a `deferrable initially deferred` constraint trigger that checks per-currency balance, entry count, and that every entry agrees with its account and transaction. |
| Derived balances | Implemented | Balances are summed from entries. No mutable balance column is authoritative anywhere. |
| Immutable financial history | Implemented | The runtime database role holds `SELECT` and `INSERT` on the ledger schema and nothing else. Immutability is a privilege, not a convention. |
| Idempotent value operations | Implemented | Unique index on `operation_type + idempotency_key` plus an intent hash. A replay with matching intent returns the original result; a replay with different intent is refused. |
| Concurrency safety | Implemented | `SERIALIZABLE` with advisory locks and `xmin` tokens. Covered by tests for concurrent redemption, claim, issuance against one balance, duplicate cancellation, and refresh-token reuse. |
| Serialization-failure retry | Operator responsibility | The server refuses rather than retrying, and returns a retryable `409`. Clients must retry with the same idempotency key. See `FRONTEND_INTEGRATION.md`. |
| Multi-party settlement | Unsupported | Value settles to a single platform account. Merchant clearing, payables, fees and settlement attribution do not exist. See the generalization audit. |
| Foreign exchange | Unsupported | Currency is explicit and never converted. |

## Cards and their lifecycle

| Capability | State | Notes |
| --- | --- | --- |
| Issuance from corporate credit | Implemented | Funding flows from a platform account through an organization's corporate credit into card inventory. |
| Distribution to a contact with no account | Implemented | Email or E.164 phone. Single-use claim and activation. |
| Bulk batches | Implemented | All-or-nothing, bounded at 100, with an asynchronous variant and retry. |
| Lifecycle transitions | Implemented | Suspend, reactivate, cancel, expire, each returning exact value. |
| Sharing and splitting | Implemented | Creating a share reserves the amount immediately; the ledger transfer happens only on successful claim. Protected links carry a 256-bit secret plus a six-digit PIN, expire in 24 hours, are single-use, and lock after five wrong attempts. |
| Reload or top-up | Unsupported | A card is funded once at issuance. |
| Scheduled or recurring grants | Unsupported | |

## Acceptance and payment

| Capability | State | Notes |
| --- | --- | --- |
| Single-use payment credential | Implemented | 60 seconds, presented as an opaque QR or a 12-digit numeric code. Carries no card, owner, amount or balance; resolved server-side. |
| Two-phase capture | Implemented | A provision reserves value without posting, then confirms for any amount up to that ceiling and releases the remainder. |
| Partial approval | Implemented | Opt-in per request, defaulting to off so no existing caller is silently under-charged. |
| Refunds | Implemented | Immutable, cumulatively capped, and enforced by a database trigger that takes the same advisory lock the application does. |
| Balance inquiry at the till | Implemented | Requires a live presented credential, does not consume it, and is rate-limited on its own budget so polling cannot starve payments. |
| Retail POS application | Reference implementation | `open-giftcard-pos` exercises the payment contract and is versioned with the others. It is not retail software: no catalogue, tax, stock, cash drawer, or offline mode. |
| Online or delayed commerce | Unsupported | The payment model assumes a card presented at a till. Channel-neutral payment intent is generalization work. |

## Identity, tenancy and authorization

| Capability | State | Notes |
| --- | --- | --- |
| Tenant isolation | Implemented | PostgreSQL Row-Level Security is the authoritative barrier. 27 tables carry it, forced everywhere it can be forced. `SECURITY.md` lists every deliberate exception and a test asserts the list against `pg_class`. |
| Verified organization scope | Implemented | A client-supplied organization header is only a candidate. The request stays unauthenticated until an active membership is resolved from the database, behind RLS, on an independent connection. |
| Organization hierarchy | Implemented | Five levels, with scope living on the role assignment rather than the role. |
| Role-based authorization | Implemented | Named permissions evaluated below the transport layer. A refusal returns 404 rather than a confirming 403. |
| Platform authority | Implemented | A separate model from customer membership. |
| Sessions | Implemented | 15-minute access tokens, rotating 30-day refresh tokens with reuse detection that revokes the whole token family and records an audit event. Lifetimes are fixed and startup fails if configuration changes them. |
| External identity providers | Unsupported | No OIDC, SAML, or external subject model. Identities are local to the deployment. |
| Account self-service | Planned | No password reset, no self-service deletion. Recovery is an operator action. |
| Data export, retention, anonymization | Planned | Named in the generalization roadmap. Nothing implemented. |

## Audit and evidence

| Capability | State | Notes |
| --- | --- | --- |
| Append-only audit | Implemented | Enforced by privilege: the runtime role has no `UPDATE` or `DELETE` on the audit schema. An audited change and its record commit atomically. |
| Denial recording | Implemented | Written on an independent connection so a refusal survives the rollback of the operation it refused. |
| Tamper-evident checkpoints | Implemented | Batches sealed behind a database sequence, canonicalised, reduced to a SHA-256 Merkle root, chained and signed. A signer outage delays sealing without ever refusing a purchase. |
| Managed key custody | Operator responsibility | The `RemoteHttp` provider signs through a custody gateway over mutual TLS so the private key stays in KMS or HSM custody. It has run only against a stubbed transport. No gateway, WORM retention, or verification drill has run anywhere. `DevelopmentFile` is refused outside Development. |

## Notifications

| Capability | State | Notes |
| --- | --- | --- |
| Transactional outbox | Implemented | Messages are queued inside the business transaction, so a message becomes durable exactly when the thing it describes does. Bounded retry, backoff and dead-lettering. |
| Email delivery | Reference implementation | An SMTP sender ships for demonstrations. Deliverability, templating and branding are yours. |
| SMS delivery | Unsupported | No adapter, by decision. Outside Development, phone distribution, direct sharing and bulk acceptance fail closed with `notification.channel.unconfigured` before any business state is committed. |
| Webhooks and integration events | Planned | No durable external event contract exists. Named in the generalization roadmap. |

## Reseller and partner channel

| Capability | State | Notes |
| --- | --- | --- |
| Partner registration and API clients | Implemented | Six endpoints covering registration, client management, disable, token exchange and minting. |
| E-pin minting | Implemented | Authenticated partners mint cards against a prepaid float. |
| Mint rate limiting | Implemented | Atomic, database-timed and RLS-isolated, so it stays exact across API replicas and cannot be multiplied by adding one. Returns 429 with `Retry-After`. |
| Ingress flood control | Operator responsibility | The authenticated quota is exact, but a coarse source-IP limit before authentication is the ingress's job. |

## Clients

| Capability | State | Notes |
| --- | --- | --- |
| Operator portal | Reference implementation | A same-origin BFF keeps refresh tokens server-side; its backend client is generated from the pinned contract at build time. |
| Cardholder application | Reference implementation | Server-rendered, ships no JavaScript bundle, English-first with a language catalogue structured for more. |
| POS till | Reference implementation | See acceptance above. |
| Branding and white-label | Unsupported | Product naming lives in source, templates and the string catalogue. Rebranding means editing them. Explicitly withdrawn from the 1.0 promise; planned afterwards. |

## Operations

| Capability | State | Notes |
| --- | --- | --- |
| One-command start | Implemented | `docker compose up` for the API, plus an overlay for the portal and cardholder. CI runs the published instructions verbatim and proves the full stack from a clean checkout. |
| Schema migration | Implemented | Forward-only, applied by a migration owner role that the application never uses. Readiness returns 503 naming modules behind the build, so an instance refuses traffic rather than serving a half-migrated database. |
| Health and readiness | Implemented | Liveness is independent of the database so an outage does not get healthy pods killed. |
| Metrics and tracing | Operator responsibility | Bounded OTLP exports and six Prometheus-compatible alert rules ship. A collector, routing and dashboards are yours. |
| Backup and restore | Operator responsibility | A restore drill and key-ring verification exist as scripts and have been run locally. Scheduling, retention and off-site custody are yours. |
| TLS, DNS, ingress, secret management | Operator responsibility | The platform refuses insecure configuration outside Development and validates forwarded-header trust. It supplies none of the infrastructure. |
| High availability | Unsupported | Data Protection keys and sessions are shared so replicas work, and a partner quota was proved exact across two hosts. No HA topology is tested or claimed. |

## The honest summary

The financial core, the tenancy barrier, and the audit trail are the parts to
trust: each is enforced by the database rather than by application code alone,
and each is covered by tests that run against real PostgreSQL.

The clients are demonstrations of the contract. The operational surface is
deliberately a set of hooks and refusals rather than a platform, because this
project cannot certify infrastructure it does not own.

And the whole system is shaped around one business model: an operator funds
organizations, organizations distribute to their people, and people spend at
tills the operator controls. Anything outside that shape is currently absent
rather than configurable. `GENERALIZATION_AND_1_0_SCOPE_AUDIT.md` is the honest
account of what that costs an adopter.
