# Architecture

## Document Status

**Status:** Phase 1 through Phase 4 complete as synchronized source candidates
**Implementation status:** IMPL-001 through IMPL-034 are implemented. Phase 4
has QR payment token issuance (ADR-017), POS client/terminal authentication
(ADR-043), time-bounded payment provisions (ADR-033, ADR-044), and atomic
redemption confirmation (ADR-018, ADR-045, ADR-046), and immutable partial
refunds (ADR-047, ADR-048), and platform POS payment reporting (ADR-049).
IMPL-033 adds the organization card register (ADR-052), and IMPL-034 adds
durable asynchronous bulk processing (ADR-051).
IMPL-001
through IMPL-021 were published as synchronized Phase 2 candidate `v0.2.0-rc.1`;
IMPL-022 through IMPL-024 are published as synchronized Phase 3 candidate
`v0.3.0-rc.1` with both clients pinned to the same contract (RELEASE-002). The
Phase 4 backend through IMPL-031, cardholder through CARD-006, and portal
through PORTAL-016 are published as synchronized `v0.4.0-rc.1` with
byte-identical client snapshots (RELEASE-003). The
finance/customer portal and recipient app have one converged, additive backend
contract for discovery, team administration, cardholder claim sessions, and
trusted proxy handling. ADR-015 and ADR-016 define the delivered Phase 3 sharing
reservation and link-protection boundaries.
See `PHASE_3_PLAN.md` for its independently shippable sequence and section 16
for delivered code.
The exact three-repository deployment contract and notification blocker are in
`docs/DEPLOYMENT.md`.

This document describes only the currently accepted architectural direction.

Unresolved architectural decisions are tracked in `docs/DECISIONS.md`.
Do not treat an unresolved decision as accepted architecture.

---

## 1. System Purpose

The system is a secure, multi-tenant digital corporate gift card platform
operated by a single platform operator.

The platform allows the platform operator to:

- Create and manage corporate customer organizations
- Assign initial organization administrators
- Allocate corporate gift card credit
- Audit administrative and financial activity

Corporate customers will eventually be able to:

- Create subsidiary organizations
- Create and manage internal users
- Define roles and permissions
- Generate and distribute digital gift cards
- Split and share gift card balances
- Redeem digital gift cards through POS-integrated dynamic QR codes

---

## 2. Architecture Style

The selected architecture style is a modular monolith.

The system will initially be deployed as one application while keeping business
modules explicitly separated.

Reasons:

- Financial operations require strong transactional consistency.
- The initial team and project scope do not justify distributed-system complexity.
- A single deployment is easier to operate during early development.
- Explicit module boundaries preserve a possible path to service extraction.
- Cross-module transactions are simpler and safer inside one process.

Microservices must not be introduced without a demonstrated scaling,
deployment, ownership, or reliability requirement.

---

## 3. Technology Stack

### Backend

- .NET 10
- ASP.NET Core Web API
- Entity Framework Core
- OpenAPI / Swagger

### Data Infrastructure

- PostgreSQL
- Redis — deferred until a measured ephemeral-state need
- Elasticsearch — deferred until a measured search/reporting need

### Authentication

- JWT access tokens
- Rotating refresh tokens
- Refresh-token reuse detection

### API Style

- REST
- Explicit request and response contracts
- URL-segment versioning under `/api/v1` (ADR-027)

---

## 4. Source-of-Truth Rules

PostgreSQL is the authoritative source of truth for:

- Organizations
- Organization hierarchy
- Users
- Organization memberships
- Roles
- Permissions
- Gift cards
- Wallet ownership
- Ledger entries
- Financial operations
- Redemption outcomes
- Authoritative audit records

Redis may be used only for temporary or derived state, including:

- Rate limiting
- Temporary verification state
- Short-lived QR tokens
- Permission caching
- Session-related cache
- Distributed coordination where explicitly required

Elasticsearch may be used only for:

- Search
- Reporting
- Audit-log querying
- Operational analytics
- Read-optimized projections

Redis and Elasticsearch must never be authoritative sources for ownership,
authorization, gift card balances, or financial transaction outcomes.

---

## 5. Intended Module Map

### Foundation Modules

- Identity
- Organizations
- Authorization
- Audit

### Financial Modules

- Ledger
- Corporate Credits
- Gift Cards
- Distribution

### Sharing and Payment Modules

- Sharing
- Payments
- Redemption

### Supporting Modules

- Reporting
- Notifications

Supporting modules must not be implemented until required by a current task.

---

## 6. Module Responsibilities

### Identity

Owns:

- Global user accounts
- Email-only staff contacts and email-or-phone recipient contacts
- Authentication credentials
- Login
- Access-token issuance
- Refresh-token lifecycle
- Session revocation
- Password reset

Does not own:

- Organization memberships
- Organization-specific roles
- Gift card ownership
- Corporate permissions

### Organizations

Owns:

- Organizations
- Parent-child organization relationships
- Organization status
- Organization memberships
- Organization lifecycle

### Authorization

Owns:

- Permission definitions
- Organization-specific roles
- Role-permission assignments
- Membership-role assignments
- Authorization-evaluation contracts

### Audit

Owns:

- Administrative audit records
- Security-sensitive activity records
- Audit-query contracts
- Database-ordered Merkle checkpoint manifests, signatures, and witness receipts

Audit records and checkpoint evidence must be append-only. ADR-013 seals bounded
committed sequence ranges asynchronously; external signing and witness calls are
never part of the audited business transaction.

### Ledger

Owns:

- Immutable financial entries
- Financial transaction identity
- Idempotency relationships
- Reconciliation data
- Balance projections or materialized-balance support

The ledger remains the financial source of truth.

### Corporate Credits

Owns:

- Corporate credit allocation use cases
- Customer-company credit availability
- Credit-allocation history

### Gift Cards

Owns:

- Gift card lifecycle
- Gift card ownership
- Gift card status
- Expiration and cancellation rules

### Distribution

Owns:

- Email/phone recipient invitations and claim lifecycle
- Assignment of corporate gift cards to recipient identities
- Immutable distribution history
- Notification delivery contract
- Bulk distribution
- Distribution batches

### Sharing

Owns:

- Partial balance sharing
- Share invitations
- Secure share links
- Share-link protection
- Claim lifecycle

Accepted Phase 3 boundary (ADR-015, ADR-016): Sharing owns active reservation,
protection-attempt, cancellation, expiration, and claim state. Ledger remains
the only authority for posted value. Creation reserves immediately; successful
claim atomically consumes the reservation, creates the child lineage, and posts
the source-to-child Ledger transfer; cancel, expiry, and terminal PIN lock
release without posting. Generic links require an authenticated recipient and
a six-digit PIN. Contact-bound invitations reuse Distribution/Identity
contracts for verified new-recipient activation without referencing either
module implementation.

### Payments and Redemption

Owns:

- Dynamic payment tokens
- POS validation
- Redemption commands
- Idempotent redemption outcomes
- Concurrent-spend protection

### Reporting

Owns read-oriented financial, reconciliation, and recipient-card queries. Its
Phase 2 implementation composes current PostgreSQL source records and owns no
schema or authoritative state.

Reporting must not modify authoritative business or financial records.

---

## 7. Module and Assembly Structure

The physical layout is accepted per ADR-004: **one project per business module,
each with a small `.Contracts` project** for its public surface.

Accepted Sprint 1 layout:

- One project per business module, containing internal `Domain`, `Application`,
  and `Infrastructure` folders.
- One small `.Contracts` project per business module (public interfaces and DTOs
  only).
- One API / Host project.
- One small `BuildingBlocks` project for shared technical primitives.

Initial modules only: Identity, Organizations, Authorization, Audit. No other
module is scaffolded until it enters the current task scope.

### Boundary Enforcement (ADR-004, ADR-011)

- Module implementation types are `internal` wherever possible.
- Another module may reference only the owning module's `.Contracts` project,
  never its implementation assembly.
- One DbContext and one PostgreSQL schema per module (`identity`,
  `organizations`, `authorization`, `audit`) on one shared database.
- Each module owns its migrations independently.
- Architecture tests enforce the dependency rules, run by CI on every push and
  pull request (ADR-022, `.github/workflows/ci.yml`).

### Dependency Direction

Regardless of layout, the following constraints are mandatory:

- Domain code must not depend on ASP.NET Core controllers.
- Domain code must not depend on HTTP-specific types.
- Domain code must not depend on Redis.
- Domain code must not depend on Elasticsearch.
- A module must not directly modify another module's internal persistence entities.
- A module must not use another module's DbContext.
- Cross-module behavior must use an explicitly defined public boundary.
- API endpoints must call application behavior instead of manipulating EF Core entities directly.
- EF Core entities must not be exposed directly through API responses.

### Cross-Module Communication (ADR-011)

- Synchronous public contract interfaces for request/response.
- Synchronous in-process integration events where decoupling is useful,
  including audit notifications. Fire-and-forget events are not used in Sprint 1.
- Administrative operations that require an audit record commit the business
  change and the audit insert **atomically**; audit failure rolls back the
  operation.
- Atomicity uses an explicit transaction coordinator that shares one physical
  Npgsql connection and PostgreSQL transaction across participating module
  DbContexts. Ambient `TransactionScope` is not the default. No message broker or
  transactional outbox in Sprint 1.

---

## 8. Multi-Tenant Isolation and Execution Context

### Tenant-Isolation Mechanism (ADR-005)

- One shared PostgreSQL database.
- A separate schema per module (module ownership is separate from tenancy).
- `organization_id` on every tenant-owned table.
- EF Core query filters for developer ergonomics and defense in depth.
- PostgreSQL Row-Level Security (RLS) as the authoritative database-level
  isolation barrier. Application-level filtering alone is not sufficient.

Entity tenancy categories must be documented per entity:

- **Global** — no `organization_id` (e.g. global user accounts, global
  permission definitions).
- **Platform-scoped** — owned by the platform scope (ADR-021).
- **Tenant-scoped** — carries `organization_id` and is subject to RLS
  (organizations, memberships, organization roles, role assignments, and
  tenant-owned business records).

### Execution Context (ADR-020)

Every tenant-scoped operation resolves a scoped `IExecutionContext` carrying
trusted server-side values, including at least:

- `UserId`
- `ActiveMembershipId`
- `ActiveOrganizationId`
- `IsPlatformOperator`
- `CorrelationId`

Rules:

- Client-supplied organization or membership identifiers are never trusted as
  proof of access.
- The application validates that the authenticated user has an active membership
  and sufficient scope for the requested operation.
- Domain and application layers must not depend on `HttpContext`.
- HTTP middleware populates the scoped context after validating the user and
  active membership; background jobs and tests populate the same abstraction
  explicitly. `AsyncLocal` is avoided unless later proven necessary.

Tenant isolation must remain enforceable outside controllers, including
application handlers, background jobs, internal module calls, and scheduled
operations.

### RLS Session Propagation (ADR-020)

- A `SaveChangesInterceptor` may stamp `organization_id`, reject mismatched
  tenant ownership, and add audit metadata — but it is not the only mechanism
  that sets RLS session context, because RLS must also protect reads.
- RLS session variables are established before any tenant-scoped SQL executes,
  via a transaction-scoped `SET LOCAL` (or equivalent safe Npgsql strategy) that
  works for reads and writes, is safe with connection pooling, and cannot leak
  one tenant's context into another request.
- Integration tests must prove pooled connections cannot reuse stale tenant
  context.

### Database Roles (ADR-019)

- A **migration owner** owns schemas, migrations, tables, policies, and grants;
  it is not used at runtime.
- A **runtime application role** is non-superuser, lacks `BYPASSRLS`, is subject
  to RLS, owns no schema, holds only required DML privileges, and has no UPDATE
  or DELETE on committed audit records (and, later, committed ledger entries).
- Platform-level cross-tenant access uses a controlled execution context and RLS
  policy path — never a superuser connection and never by disabling RLS.

---

## 9. User and Membership Model

A user account and an organization membership are separate concepts.

A user may have different roles in different organizations.

Conceptual relationship:

```text
User
└── OrganizationMembership
    ├── Organization
    └── MembershipRoles
```

Disabling an organization membership must prevent access to that organization
without necessarily disabling the global user account.

Historical records must continue referencing the original user and membership
where appropriate.

Platform staff use global user identities with separate platform-role assignments
and do not participate in the customer organization hierarchy (ADR-021).
Customer users use organization memberships and organization-role assignments.

---

## 10. Authorization Model

The system uses organization-scoped role-based access control with named
permissions.

Authorization must not depend solely on broad role names such as:

- Admin
- User

Example permission codes:

```text
organization.view
organization.update
organization.create_subsidiary

user.view
user.create
user.update
user.disable

role.view
role.create
role.assign
role.manage_permissions

audit.view
```

### Hierarchy-Aware Scope (ADR-006)

Authorization scope is stored on the membership-role assignment, not on the
role, so a role is reusable with different scopes.

- **MembershipRoleAssignment**: `MembershipId`, `RoleId`, `ScopeType`,
  `AnchorOrganizationId` (when applicable).
- **MembershipRoleAssignmentScopes**: `MembershipRoleAssignmentId`,
  `OrganizationId` — the explicit selected-organization list.

Scope types:

- `Organization` — the anchor organization only.
- `Subtree` — the anchor organization and all descendants (via the ltree path).
- `SelectedOrganizations` — one or more explicitly granted organizations.

Effective authorization evaluates the authenticated user, the active membership,
assigned organization roles, the assignment scope, the target organization, and
the organization hierarchy path. Parent-organization ownership alone never
grants access.

### Platform Authorization (ADR-021)

Platform-global authorization is a **separate platform-role assignment model**
for platform operators — it is not represented as an organization-membership role
assignment. Only authorized platform operators may hold platform-global
permissions, and being a platform operator does not automatically imply every
platform permission. The implemented model uses global `platform_roles`,
`platform_role_permissions`, and `platform_role_assignments` tables. JWT
authentication resolves their effective permission union from PostgreSQL; the
JWT subject itself never grants platform authority.

## 10a. Organization Hierarchy (ADR-010, ADR-021)

- The platform operator is a distinct platform scope, not a normal customer organization row.
- Customer organizations and their subsidiaries form their own hierarchy; the
  platform scope is not counted in the depth limit.
- Maximum initial customer hierarchy depth is 5, configurable but enforced
  server-side at subsidiary creation. Cycles are forbidden.
- Stored as `parent_organization_id` plus a materialized `ltree` path, with a
  stored depth value where useful. Reparenting updates descendant paths
  atomically.
- If `ltree` is unavailable in the target environment, the recorded fallback is
  adjacency list plus recursive CTE; the model must not change silently.

## 10b. Identifier Strategy (ADR-012)

- UUID v7 internal primary keys, exposed directly through APIs unless a separate
  human-facing reference is required.
- Separate short/formatted identifiers only for organization codes, gift card
  references, commercial references, POS transaction references, and financial
  display references.
- Sequential numeric identifiers are never public credentials or access tokens.

---

## 11. Financial Architecture

All balance-changing operations must create immutable ledger records.

Examples:

- Corporate credit allocation
- Gift card issuance
- Gift card distribution
- Balance transfer
- Balance sharing
- POS redemption
- Refund
- Expiration
- Cancellation or reversal

A mutable balance column must never be the sole financial source of truth.

Read-optimized or materialized balances may be introduced, provided they can be
reconciled against the immutable ledger.

Financial operations must be:

- Atomic
- Idempotent
- Auditable
- Concurrency-safe
- Reconciliable

Monetary values must use `decimal`, never `float` or `double`.

Currency must be explicit.

---

## 12. Initial Deployment Shape

```text
Clients
   |
ASP.NET Core Application
   |
--------------------------------
PostgreSQL
Redis
Elasticsearch — deferred
--------------------------------
```

Elasticsearch must not be introduced during the foundation phase unless an
accepted task explicitly requires it.

### Local Infrastructure (Sprint 1)

- Docker Compose contains PostgreSQL only. Redis and Elasticsearch are not added
  until a current task requires them.
- Integration tests use real PostgreSQL through Testcontainers or the guarded
  external-database mode for machines without virtualization; EF Core InMemory
  and SQLite are not substitutes for RLS and PostgreSQL-specific tests.
- Development secrets and connection strings must not be committed.

### Test Strategy (ADR-022)

Three test projects will exist once scaffolding begins: `UnitTests`,
`IntegrationTests`, `ArchitectureTests`. The integration harness runs against
real PostgreSQL via Testcontainers and `WebApplicationFactory`, exercises the
actual RLS policies through the runtime application database role (not the
migration owner), and uses explicit platform and tenant execution contexts.
Architecture tests enforce the module and layering dependency rules. Per-module
test projects are deferred until test volume justifies them.

---

## 13. Initial Implementation Order

1. Resolve architecture-blocking decisions.
2. Scaffold the modular solution.
3. Establish local PostgreSQL infrastructure.
4. Implement Identity foundations.
5. Implement Organizations and memberships.
6. Implement Authorization.
7. Implement append-only Audit.
8. Validate tenant isolation with integration tests.
9. Introduce Ledger with the first financial use cases.
10. Add Corporate Credit, Gift Card, and Distribution behavior.
11. Add Sharing.
12. Add dynamic QR and POS redemption.
13. Add reporting projections and Elasticsearch where justified.

---

## 14. Architecture Validation Priorities

The first implementation must eventually prove:

- A platform administrator can create a customer organization.
- An initial organization administrator can be assigned.
- A company administrator can create users within the permitted organization.
- A user without the required permission is denied.
- An unrelated tenant cannot read or mutate another tenant's data.
- Cross-organization role assignment is rejected.
- Disabled memberships cannot perform organization operations.
- Administrative operations produce audit records.

---

## 15. Open Architecture Decisions

See `docs/DECISIONS.md`.

All decisions blocking Sprint 1 scaffolding were resolved in PLAN-001 (ADR-004,
005, 006, 010, 011, 012, 019, 020, 021, 022). Scaffolding may proceed under
`docs/NEXT_TASK.md`.

ADR-013 was accepted in PLAN-005 and implemented provider-neutrally in IMPL-032.
The managed KMS/HSM and immutable-storage provider remain deployment selections,
not architecture decisions the application core may guess.

PLAN-004 accepted the Phase 4 payment-credential decisions: ADR-017 fixes an
opaque, PostgreSQL-backed, single-use 60-second QR token resolved by server-side
lookup; ADR-018 makes that server-issued token the identity of a purchase for
idempotency; and ADR-043 keeps POS software an API client that never receives
database credentials. IMPL-025 implements ADR-017 issuance; ADR-018 redemption,
ADR-033 provisions, and ADR-043 POS authentication remain unimplemented.

ADR-015 and ADR-016 were accepted for Phase 3 by PLAN-003. Their delivery
sequence is recorded in `PHASE_3_PLAN.md`. IMPL-022 delivers authenticated
protected links, IMPL-023 delivers verified direct-recipient activation, and
IMPL-024 delivers authoritative history and reconciliation.

---

## 16. Implemented Architecture (IMPL-001 through IMPL-034)

This section records only what exists in code, to keep the accepted direction
above distinguishable from delivered work.

### Solution layout

```text
src/
  GiftCardPlatform.Api                      host, endpoints, JWT bearer auth, Phase 3 console
  GiftCardPlatform.BuildingBlocks           execution context, transaction coordinator, session context, errors
  GiftCardPlatform.Modules.Identity         users, password login, JWT sessions, refresh rotation
  GiftCardPlatform.Modules.Organizations    Domain / Application / Infrastructure
  GiftCardPlatform.Modules.Authorization    roles, grants, scoped assignments, evaluator
  GiftCardPlatform.Modules.Audit            Domain / Application / Infrastructure
  GiftCardPlatform.Modules.Ledger           immutable accounts, transactions, entries
  GiftCardPlatform.Modules.CorporateCredits allocation, reversal, balance/history
  GiftCardPlatform.Modules.GiftCards        funded issuance, inventory, ownership/provenance
  GiftCardPlatform.Modules.Distribution     recipient invitation, claim, activation history
  GiftCardPlatform.Modules.Sharing          reservations, protected/direct invitations, claim/cancel/expiry
  GiftCardPlatform.Modules.Reporting        read-only finance, reconciliation, owned-card history
  GiftCardPlatform.Modules.Payments         QR payment tokens, POS client/terminal identity
  <module>.Contracts                        public surface per module
tests/
  GiftCardPlatform.UnitTests
  GiftCardPlatform.IntegrationTests
  GiftCardPlatform.ArchitectureTests
infra/postgres/init/                        role and privilege provisioning
```

Central build and package configuration live in `Directory.Build.props` and
`Directory.Packages.props`. Nullable reference types and implicit usings are on;
warnings are not errors, so generated migrations do not create build friction.

### Implemented modules

| Module        | State                                                                 |
| ------------- | --------------------------------------------------------------------- |
| Organizations | Customer hierarchy, memberships, hierarchy queries, tenant-root resolution, hierarchy-aware RLS |
| Audit         | Append-only records, active-membership attribution, independent denial writes, forced tenant RLS, signed Merkle checkpoints and immutable-witness contract |
| Authorization | Customer roles/scopes, platform roles/bootstrap, permission evaluator, hierarchy-aware RLS |
| Identity      | Global users, password hashing, JWT sessions, rotating hashed refresh tokens, create/disable services |
| Ledger        | Posted balanced accounts/transactions/entries, idempotent writer, reversal and balance contracts |
| Corporate Credits | Allocation, balance/history, immutable compensating reversal |
| Gift Cards    | Root-funded issuance, organization inventory, ownership/provenance, tenant/owner RLS |
| Distribution  | Email/phone invitation, single-use claim, immutable events, tenant/claimant RLS |
| Sharing       | Exact-owner reservations, protected link/PIN and contact-bound invitation lifecycle, child-card claim, forced RLS |
| Payments      | Opaque single-use 60-second QR credentials, platform-scoped POS client/terminal identity and device tokens, two-minute provisions, and atomic idempotent redemption into per-currency settlement |

### Persistence

- One shared PostgreSQL database; `identity`, `organizations`, `authorization`,
  `audit`, `ledger`, `corporate_credits`, `gift_cards`, `distribution`, and `sharing`
  schemas, each with its own DbContext and independent migration history table.
- `Organization`: UUID v7 identity, unique normalized `code`, `ltree`
  `hierarchy_path`, `depth`, nullable `parent_organization_id`, and check
  constraints for root/depth consistency, non-negative depth, the ADR-010 depth
  ceiling, and self-parenting.
- `AuditRecord`: UUID v7 identity, actor user/type, active membership when the
  actor is an organization member, organization scope, operation, entity
  reference, outcome, correlation id, a database sequence, and a small `jsonb`
  metadata payload. Writers hold a shared transaction advisory lock; the
  checkpoint worker briefly holds its exclusive counterpart to freeze a fully
  committed boundary without serializing writers against each other.
- `AuditCheckpoint`: a versioned SHA-256 Merkle root over canonical leaves,
  chained to the previous manifest digest. A separate ECDSA P-256 seal records
  public verification material, and a separate receipt names the exact signed
  manifest published through `IAuditCheckpointWitness`. Verification compares
  the witness's dedicated immutable inventory with database receipts, so a
  database actor cannot hide deletion by removing both a checkpoint and receipt.
- `GiftCard`: UUID v7 business identity, non-credential public reference,
  funding and issuing organizations, current owner/lifecycle, dedicated ledger
  links, initial value/currency, validity and policy, source/root lineage,
  idempotency identity, and issuing user/membership.
- `DistributionInvitation`: immutable tenant/card/contact/token-hash identity,
  bounded failed attempts, expiry and claim state, distribution/claim
  idempotency, and actor attribution. `DistributionEvent` is append-only.
- `GiftCardShare`: immutable kind/source/funding/sender/amount/token-hash and
  protected PIN hash or contact-bound normalized/masked recipient identity,
  pending/claiming/claimed/cancelled/expired/locked state, bounded failed
  attempts, idempotency keys, child and Ledger links, identity-created outcome,
  and lifecycle timestamps. `GiftCardShareEvent` is append-only.

### Cross-module atomicity

`ITransactionCoordinator` opens one physical Npgsql connection and one explicit
PostgreSQL transaction. `ScopedDatabaseConnection` is the shared connection every
module DbContext is configured with, so `IModuleTransaction.EnlistAsync` can join
each context to the same transaction. The Organizations service writes the
organization and then calls `IAuditRecorder` — the Audit module's public contract
— which enlists its own DbContext and writes inside the same transaction.
Anything that throws before commit rolls both writes back on disposal. No
ambient `TransactionScope`, no outbox, no fire-and-forget events.

IMPL-013 uses the same mechanism across Distribution, Gift Cards, Identity, and
Audit. Distribution commits invitation, ownership transition, event, and audit
before calling the notification boundary. Claim commits identity
association/creation, card ownership, invitation state, event, and audit in one
serializable transaction. The Phase 2 sink is in-memory and cannot stand in for
production delivery durability; real provider retry/outbox behavior is
explicitly deferred.

IMPL-022 spans Sharing, Gift Cards, Ledger, and Audit in one serializable
transaction. Create locks card value before calculating active reservations.
Claim consumes one reservation, creates the child account/card, posts one
balanced `gift_card.share_transfer`, completes the share, and records audit
before commit. Cancellation, terminal PIN lock, expiry, and source-card
termination close reservations without a financial posting. PostgreSQL
serialization and uniqueness races surface as retryable HTTP 409 responses.

IMPL-023 uses the same atomic path after its contact-bound token verifies. The
Identity-owned contact service normalizes/masks before the sender transaction;
the invitation and reservation commit before the notification boundary runs.
Activation resolves or creates the recipient through the existing Phase 2
Identity service, promotes the exact anonymous share candidate to that identity,
and completes the same child-card/Ledger/audit transaction. New identities may
receive a claim session; existing identities never do and continue through
normal login. The Development sink is proof only, not production delivery
durability.

### Execution context and session propagation

`IExecutionContext` is a scoped service, not `AsyncLocal`. `SessionContextWriter`
issues `set_config(..., is_local => true)` with bound parameters at the start of
every module transaction, so the values cover reads as well as writes, are
discarded when the transaction ends, and cannot leak across pooled connections.
Tenant-owned policies consume `app.organization_id` and
`app.is_platform_operator`. Anonymous claim additionally sets the exact parsed
`app.claim_invitation_id` as a narrow candidate selector. The invitation's
256-bit secret is then verified in constant time before the context is promoted
to the recipient identity; the candidate ID alone grants no card ownership.
Protected and direct share claim similarly set only the parsed `app.share_id`
candidate. A generic candidate retains its authenticated recipient; a direct
candidate remains anonymous until its contact-bound secret resolves through
Identity. Gift Cards, Ledger, and Sharing policies admit the minimum
source/plan/child rows needed for that one claim; the token (and generic PIN)
hash is still verified in constant time before value moves.
PostgreSQL resolves the selected organization's root and evaluates row ownership
against that root. Authentication separately keeps the verified tenant root in
`IExecutionContext` for root-keyed financial EF filters. The session-context
mechanism makes isolation hold for reads as well as writes.

### Database roles

`infra/postgres/init/01-roles-and-privileges.sh` creates the migration owner and
the runtime application role, both `NOSUPERUSER NOBYPASSRLS`. Module schemas are
created there rather than only by EF migrations so that
`ALTER DEFAULT PRIVILEGES` can be attached up front: every table the migrator
later creates automatically grants the runtime role SELECT/INSERT/UPDATE/DELETE
in `identity`, `organizations`, and `authorization`, but only SELECT/INSERT in
`audit`, `ledger`, and `corporate_credits`. Gift Cards has SELECT/INSERT/UPDATE
for controlled ownership and lifecycle transitions but no DELETE, so provenance
cannot be erased. Distribution invitations allow controlled state updates but
database triggers protect identity fields; distribution events cannot be
updated or deleted. Restricted financial grants preserve immutable history;
the restricted audit grant makes audit append-only.
Sharing allows SELECT/INSERT/UPDATE but no DELETE; triggers make event history
append-only and protect immutable share identity and credential fields.

### Audit isolation and actor attribution (IMPL-011A)

`audit.audit_records` uses enabled and forced RLS. Customer visibility follows
the tenant root rather than exact active-organization equality, the controlled
platform path can read across customers, and an identity-only user can read its
global rows plus organization-scoped card audit only when it owns a card funded
by that organization. A runtime connection with no execution context sees no
audit history.

An organization-member audit row requires `actor_membership_id` alongside the
global actor user and organization scope. Atomic writes use the shared business
transaction. Denial records use an independent connection and transaction but
receive the same transaction-local execution context before inserting, so they
survive the refused operation without bypassing RLS (ADR-025, ADR-032).

### Authentication

The Identity module owns global users, fixed-lifetime sessions, and one-time
refresh credentials in the `identity` schema. Staff/admin provisioning remains
email-only; a recipient identity has exactly one globally unique normalized
email or E.164 phone contact (ADR-034). Passwords use the ASP.NET framework
hasher and the ADR-028 policy: 12–128 Unicode characters, no composition rules,
and common-password rejection. Passwords and refresh-token plaintext are never
persisted or audited.

Login accepts the account's email or phone and issues a signed 15-minute JWT
plus a 30-day opaque refresh token. Login and refresh send no notification.
Refresh rows store only SHA-256 token hashes. Rotation locks the presented
PostgreSQL row with `FOR UPDATE`, consumes it exactly once, and inserts its
replacement in the same session family. Concurrent reuse sees the committed
consumed state and revokes the full session family. Login is fixed-window
rate-limited by source IP. User disablement revokes every active session;
explicit revoke is possession-based and idempotent.

JWT bearer validation is active in every environment. Without an
`X-Organization-Id`, authentication resolves the subject's effective persisted
platform-role permissions; no assignment means an authenticated identity with
no platform authority. Supplying `X-Organization-Id` instead requests an
organization context, which authentication completes only after resolving the
user's active membership and tenant root behind RLS. The active organization
anchors permission evaluation; the tenant root drives customer isolation and
root-keyed financial filters (ADR-031). Access tokens remain usable for at most
their 15-minute lifetime after session revocation, as accepted in ADR-028.

Authorization is enforced in the application service by named permission, below
the controller layer, so it remains valid outside HTTP.

Caller-controlled development identity and permission headers were retired in
IMPL-008. Signed JWT bearer authentication is now the only authentication
mechanism in every environment.

### Development demonstration console

A single self-contained HTML page is served at `/demo`, mapped only from the
Development branch of the pipeline (embedded as a resource in the API assembly).
It demonstrates one-time bootstrap, email/phone login, platform customer
onboarding, initial Company Administrator assignment, customer
hierarchy/membership/RBAC operations, corporate-credit and issuance workflows,
individual distribution, local activation delivery, recipient claim, and
protected-share creation/claim/history/cancellation, and refresh/revoke. It
distinguishes posted, reserved, and available card value and uses the public API
with `Authorization: Bearer` and, for
customer operations, `X-Organization-Id`; there is no private UI backend path.
Password, bootstrap-secret, and claim-token values are not written to activity
or result history. Session tokens and locally useful demonstration identifiers
live only in browser `sessionStorage`. The route and local delivery lookup are
**absent outside Development** (404), while the API and bearer authentication
remain environment-independent.

### Organization memberships and Row-Level Security (IMPL-002)

`OrganizationMembership` is the first **tenant-owned** table
(`organizations.organization_memberships`): UUID v7 identity, `organization_id`
(tenant owner, FK to `organizations`), a global Identity `user_id` reference
(without a cross-module database FK), an Active/Disabled status, UTC
created/disabled timestamps, and
a unique `(organization_id, user_id)` constraint.

Tenant isolation is enforced by a PostgreSQL RLS policy with
`FORCE ROW LEVEL SECURITY`, so even the table owner is subject to it. The policy
reads the session context established per transaction by `SessionContextWriter`:

- `USING` admits a customer row when its organization belongs to the tenant root
  resolved from `app.organization_id`, or when `app.is_platform_operator` is
  true. This is the controlled cross-tenant read path for platform operators.
- `WITH CHECK` normally admits a written row only when its organization belongs
  to that same tenant root. Its only platform exception requires both persisted
  platform authority and the transaction-local
  `app.is_initial_admin_bootstrap=true` flag. Application code can set that flag
  only inside the atomic initial Company Administrator workflow, after requiring
  its named platform permission and validating an active root organization.

An EF Core global query filter mirrors the policy through
`organizations.organization_belongs_to_caller_tenant(uuid)`. RLS — not this
filter — is the authoritative barrier; isolation tests query with the
application filter deliberately absent.

`IMembershipService` (Organizations module) exposes create, list, and disable.
The target organization comes from the route and is authorized against the
verified active membership; a root membership may reach a descendant only when
an explicit `Subtree` or `SelectedOrganizations` assignment covers it. Create
and disable each commit their membership row and an append-only audit record in
one transaction. Permissions are named: `organization.memberships.create`,
`organization.memberships.view`, and `organization.memberships.disable` for
customer callers, and `platform.organizations.memberships.view` for the platform
read path.

The runtime application role remains `NOSUPERUSER NOBYPASSRLS`, so it is fully
subject to the policy. Integration tests exercise the policy through that runtime
role and prove: cross-tenant read, insert, update, and delete are all denied; the
platform read path works; pooled connections cannot reuse a prior tenant's
context; and membership writes commit atomically with their audit record.

### Subsidiary creation and the organization hierarchy (IMPL-003)

`Organization.CreateSubsidiary` produces a child one level below its parent: a
UUID v7 identity, `parent_organization_id` set, `depth` = parent depth + 1, and an
`ltree` `hierarchy_path` extending the parent's materialized path (ADR-010). No
new table or migration was required — the columns and check constraints have
existed since IMPL-001.

The depth limit is **configurable** via `Organizations:Hierarchy:MaxDepth`
(`OrganizationHierarchyOptions`) and validated at registration against the
database ceiling enforced by `ck_organizations_max_depth`, so configuration can
never ask for a depth the database would reject; startup fails fast otherwise.
Depth is zero-based, so the default of five levels admits depths 0–4. Cycles are
structurally impossible here because a new organization is always a fresh leaf;
reparenting — and therefore descendant-path maintenance and cycle detection — is
out of scope.

`ISubsidiaryService` exposes create and list. Both are **organization-scoped**:
the parent comes from the route, must be inside the caller's tenant, and must be
covered by the active membership's named permission scope. The request body
carries only name and code. Platform operators do not create customer
subsidiaries in this slice. Permissions are
`organization.create_subsidiary` and `organization.view`. Creation commits
the organization row and its append-only audit record
(`organization.subsidiary.created`, scoped to the parent) in one transaction.
Listing returns **direct children only**; subtree traversal belongs with ADR-006
scope evaluation.

### Authorization module (IMPL-004)

The original authorization slice introduced five tables (ADR-006). Four are
tenant-owned and carry their operational `organization_id` directly,
denormalized onto grant and scope rows. Their forced RLS policies now use the
shared stable SECURITY DEFINER tenant predicate so a parent administrator may
work in an explicitly authorized descendant without admitting another customer
tenant (ADR-031).

`permissions` is the fifth: a **global** catalogue with no tenant key and no RLS,
seeded from the permission constants when migrations run, so names have one
source of truth. `role_permissions` carries a foreign key to it, which is what
rejects an unknown permission name at the database rather than in application
validation alone.

```text
Role (owned by one organization, no scope of its own)
  └── RolePermission                    granted permission names
MembershipRoleAssignment                membership + role + ScopeType + anchor
  └── MembershipRoleAssignmentScope     explicit organizations (SelectedOrganizations only)
```

**Scope lives on the assignment, never on the role**, so one role definition is
reusable at different scopes. `IPermissionEvaluator` resolves effective
permissions as the union over every assignment whose scope covers the target
organization:

- `Organization` — the anchor only.
- `Subtree` — the anchor and all descendants, tested through
  `IOrganizationHierarchyQuery` on the Organizations contract, so Authorization
  never reads another module's tables (ADR-004). Containment is evaluated against
  the materialized `ltree` path.
- `SelectedOrganizations` — exactly the listed organizations.

Parent-organization ownership alone grants nothing: only an assignment that
actually reaches the target does.

The evaluator runs its scope checks inside its own transaction, and the hierarchy
lookup **joins it as a nested scope** (ADR-026). This is the first genuine
cross-module nested caller, and it exposed a bug in the coordinator: a
committed-but-not-yet-disposed transaction still reported as `Current`. A
transaction now stops being ambient the moment it commits, cleared by reference
so a scope that commits then disposes cannot clear a different transaction
started in between.

Three escalation paths are refused in `RoleService`: a platform permission cannot
be granted to an organization role; a caller cannot grant a permission it does
not itself hold; and a role owned by another organization is invisible, so the
refusal is 404 rather than a 403 that would confirm its existence.

Organization service enforcement is database-backed (IMPL-005).
`IOrganizationPermissionAuthorizer` evaluates the verified active membership
against the operation's target organization through `IPermissionEvaluator`.
Services begin their transaction before authorization, so the permission decision
and the business read/write share one transaction rather than leaving a
permission-revocation race. The former development organization-permission header
is ignored.

### Persistent platform authorization and initial administrators (IMPL-007)

Platform authority is persisted separately from customer RBAC:

```text
PlatformRole
  └── PlatformRolePermission
PlatformRoleAssignment               global user + platform role
```

`IPlatformPermissionResolver` derives a JWT subject's permissions from those
tables on every authentication request. Platform-role rows are global and do
not use tenant RLS. There is currently no general platform-role management API.

The first platform administrator is created through
`POST /api/v1/bootstrap/platform-administrator`. The anonymous endpoint is
rate-limited and requires a configuration secret in a dedicated header. The
secret is validated in constant time and is never persisted or audited. A
singleton `platform_bootstrap_state` row is locked with `FOR UPDATE`; user,
built-in Platform Administrator role, every known platform permission,
assignment, completion state, and audit records commit in one shared
transaction. The durable completion row makes later and concurrent attempts
fail closed.

An operator with
`platform.organizations.initial_administrators.assign` may then assign an
existing active user as a root customer's first Company Administrator. The
workflow atomically creates or reuses the active membership, creates a built-in
organization role with every known organization permission, and assigns it with
`Subtree` scope anchored at the root. A durable
`organization_administrator_bootstraps` row makes the same request idempotent
and refuses a different second initial administrator. The narrow RLS write flag
described above exists only for this cross-tenant bootstrap transaction.

### Ledger and first corporate-credit allocation (IMPL-009)

ADR-014 is implemented as a posted-only balanced double-entry product ledger.
The Ledger module owns `accounts`, `transactions`, and `entries`; each account
has one currency and may represent platform funding, organization corporate
credit, or one gift card. Each entry is a positive debit or credit, and every
transaction must balance independently per currency. A deferred PostgreSQL
constraint trigger verifies the complete posting set at commit. The runtime role
has only `SELECT` and `INSERT` on the ledger schema, so committed financial
history cannot be updated or deleted.

Corporate Credits owns the allocation business record and calls Ledger only
through `ILedgerWriter`. It never selects account identifiers itself. An
allocation transfers value from the currency-specific platform funding
account to the recipient root customer's corporate-credit account. The
allocation row, two ledger entries, and audit record share one cross-module
`SERIALIZABLE` transaction.

`operation_type + idempotency_key` is unique in Ledger and the allocation key is
also unique in Corporate Credits. An identical completed retry returns the
original result; reuse for changed financial intent is a conflict. A
serialization conflict is safe to retry with the same key.

Ledger and Corporate Credits use separate `ledger` and `corporate_credits`
schemas, DbContexts, migration histories, EF query filters, and forced RLS.
Tenant rows are keyed by the root customer organization. Platform funding
accounts carry no tenant id and are accessible only through the platform RLS
path.

### Corporate-credit balances and allocation history (IMPL-010)

Corporate-credit reads are exposed through `ICorporateCreditQueryService`.
Platform callers require `platform.corporate_credits.view`; organization callers
require `organization.corporate_credits.view`, evaluated against their verified
membership and role-assignment scope below HTTP.

Balances cross the Ledger boundary through `ILedgerBalanceQuery` and are
calculated as credits minus debits for each currency-specific organization
account. No mutable balance column or independent financial projection is
authoritative. Organizations without an account receive an empty balance list.

Allocation history remains owned by Corporate Credits. It is ordered by
server-controlled timestamp and UUID, uses a stable opaque cursor, and returns
only explicit contracts. EF query filters and forced PostgreSQL RLS enforce the
same tenant scope. A permission-data migration adds both view permissions and
upgrades existing built-in Platform Administrator and Company Administrator
roles; catalogue synchronization keeps later bootstraps current.

### Corporate-credit allocation reversal (IMPL-011)

An allocation correction is a new immutable `corporate_credits.reversals`
record and a new Ledger transaction with the exact opposite postings. Ledger
stores `reverses_transaction_id`, protects it with a unique self-reference, and
never updates the original allocation, transaction, or entries.

Reversal requires `platform.corporate_credits.reverse`. Corporate Credits owns
the business reason and one-to-one allocation relationship; Ledger independently
validates that the referenced transaction is an allocation for the same tenant.
Both modules use operation-scoped idempotency keys, and Corporate Credits
returns the original response only when the repeated intent is identical.

Before debiting corporate value, Ledger takes a transaction-scoped PostgreSQL
advisory lock derived from organization ID and currency, then calculates the
available account balance from entries. The lock is shared by later
value-consuming operations. It avoids granting UPDATE solely to obtain a row
lock, preserving runtime `SELECT`/`INSERT` financial privileges. The reversal
record, compensating entries, and audit record commit in one cross-module
`SERIALIZABLE` transaction.

### Phase 2 vision and hierarchy alignment (IMPL-011A)

Authentication now resolves an `ActiveMembershipResolution` containing both the
membership ID and its root customer organization. `ActiveOrganizationId`
remains the operational membership owner; `TenantRootOrganizationId` is the
isolation and funding boundary. Organization-owned EF filters translate the
shared PostgreSQL tenant predicate, while Ledger and Corporate Credits compare
their root-keyed rows directly with the verified root. Membership and
authorization RLS policies use the same root predicate.

This change does not turn hierarchy into authority. Every service continues to
authorize the exact route target through `IOrganizationPermissionAuthorizer`;
new PostgreSQL tests prove that explicit subtree scope reaches descendant
membership and role operations while unrelated customers remain invisible.

ADR-030 fixed the Phase 2 card boundary before its schema was introduced:
root-funded
organization inventory, separate funding and issuing organizations,
accountless email/phone invitation and claim, required expiry, conservative
transfer/divisibility defaults, return-to-funding-root cancellation/expiration,
and synchronous all-or-nothing bulk operations capped at 100. ADR-033 reserves
first-class payment provisions for Phase 4 so payment reservation is not
mistaken for a posted ledger movement.

ADR-035 fixes the IMPL-014 authority and transition boundary: organization
administrators may cancel only before claim, identity owners may
suspend/reactivate only their own cards, and the platform operator retains explicit emergency
post-claim cancellation authority. Suspension preserves ownership/value and
reactivates according to ownership state. Cancellation and expiration are
terminal and use one idempotent Ledger-derived remaining-value return.
Expiration is effective at server time and is financially finalized through
PostgreSQL coordination rather than Redis or a second balance model.

### Gift-card issuance into organization inventory (IMPL-012)

Gift Cards is an independent module with its own contracts, domain,
application services, `gift_cards` schema, DbContext, migration history, query
filter, and enabled/forced PostgreSQL RLS. The policy admits rows in the
caller's verified customer tenant or rows whose exact `owner_user_id` is the
identity-only caller. A context-free runtime connection sees no cards. RLS is a
visibility boundary; issuance and inventory still require
`organization.gift_cards.issue` or `organization.gift_cards.view` against the
exact route target.

Issuance begins in `GiftCardIssuanceService` at `SERIALIZABLE` isolation. The
service records the verified `TenantRootOrganizationId` as
`funding_organization_id`, records the permission-checked route target as
`issuing_organization_id`, and initially owns the card through that
organization's inventory. Ledger alone selects the root corporate-credit
account, takes the same root/currency advisory lock used by reversals, derives
its available balance, creates one `GiftCardValue` account, and posts the
balanced `gift_card.issuance` debit/credit transaction. Card, account,
transaction, entries, and membership-attributed audit commit together.

The card stores initial funded value only as issuance provenance; current and
future balances remain ledger-derived. `valid_from_utc` defaults to the ledger
posting time, `expires_at_utc` is required, and transferability/divisibility
default to false. Root cards set `root_gift_card_id = id`,
`source_gift_card_id = null`, and `generation = 0`, preserving the lineage
fields Phase 3 needs. The `GC-` public reference contains 80 random bits but is
explicitly display/support data and is never accepted as a payment credential.

Tenant-scoped idempotency returns the original card only for identical intent.
The Ledger key is a hash-derived namespace of tenant root plus caller key, so
unrelated customers may safely choose the same external key. Cursor-paginated
inventory returns only `OrganizationInventory` cards owned by the exact target
organization. The Development Money workspace exercises both issuance and
inventory through the public API.

### Individual distribution and recipient activation (IMPL-013)

Distribution is an independent module with contracts, domain, application,
`distribution` schema, DbContext, migration history, and enabled/forced RLS.
An invitation records the funding and issuing organizations, exact card,
normalized email or E.164 phone contact, SHA-256 claim-secret hash, immutable
business identity, state, expiry, bounded failed attempts, idempotency, and
actors. Separate `DistributionEvent` rows preserve the append-only
`Distributed`, `ClaimFailed`, `Claimed`, and `ClaimExpired` history.

`GiftCardDistributionService` requires
`organization.gift_cards.distribute` for the exact route organization and an
eligible card in that organization's inventory. Under `SERIALIZABLE` isolation
it creates a Pending invitation, moves the card to ownership/lifecycle
`AwaitingClaim`, appends history, and records membership-attributed audit. Card
ownership changes, but the dedicated ledger account and entries do not.
Identical retries return the invitation; changed intent conflicts. The
256-bit raw token exists only at the notification boundary. The Development
sink captures one activation URL in memory after commit.

`GiftCardClaimService` parses only the invitation UUID before setting the narrow
claim-candidate RLS context. It row-locks that invitation and verifies the
secret hash in fixed time. Invalid secrets increment the bounded counter and
event history; five failed attempts lock the invitation, while the HTTP
endpoint also defaults to 10 source-IP requests per minute. The token defaults
to 24 hours and is single-use.

Successful claim reuses an active identity with the same contact without
changing its password, or creates the minimum email/phone identity using the
normal password policy. It then promotes the execution context to that identity
and atomically changes the card to `IdentityOwned`/`Active`, completes the
invitation, appends the claim event, and records audit. A recipient receives no
organization membership or role. Exact claimant and tenant RLS paths expose
only the invitation/card/audit data their contexts own.

ADR-034 keeps bootstrap, platform, and organization staff creation email-only.
A recipient has exactly one email or phone login contact. Only activation uses
the notification channel; later password login and the existing 15-minute
JWT/rotating 30-day refresh session generate no email or SMS. The Development
console exposes the complete send, local-delivery, claim, and recipient-login
journey without a privileged UI shortcut.

### Cardholder claim session and trusted proxy boundary (IMPL-019)

The successful claim response has an additive optional session. It is populated
only when claim created the recipient identity. Identity owns password
verification and token issuance through a narrow contract; session, refresh
credential, new identity, ownership transition, invitation completion, and
audit all join the same outer `SERIALIZABLE` transaction. Failure rolls the
complete claim back. A completed new-identity replay verifies the submitted
password before issuing a fresh session.

An existing-identity claim remains a passwordless ownership transition but
returns no session. This prevents possession of one invitation from
authenticating the account and exposing its other cards. Both outcomes continue
to return only the masked contact. The cardholder BFF consumes a returned token
pair server-side and creates its `HttpOnly` browser session; the backend refresh
token never enters browser JavaScript.

Source-IP partitions may use one `X-Forwarded-For` address only when the
immediate proxy's literal address is configured in
`Networking:ForwardedHeaders:KnownProxies`. The forwarding limit is one, and
the BFF must overwrite the outbound header from its observed connection rather
than append or relay browser input. An untrusted proxy or an empty allowlist
leaves the direct remote address authoritative.

### Gift-card lifecycle, cancellation, and expiration (IMPL-014)

`GiftCardLifecycleService` is the single transition orchestrator for company,
Platform, cardholder, and trusted expiration actors. Company calls require
`organization.gift_cards.lifecycle.manage` against the exact issuing
organization and tenant root; cardholders must be the exact claimed owner;
Platform calls require `platform.gift_cards.lifecycle.manage`. Companies cannot
cancel after claim, while the platform operator retains the accepted emergency path.

Every command normalizes a reason/idempotency intent and executes at
`SERIALIZABLE` isolation. A transaction-scoped card advisory lock serializes
state changes with claim. Suspend/reactivate never alter ownership or Ledger
value. An awaiting-claim card resumes to `AwaitingClaim`; organization- or
identity-owned cards resume to `Active`. Cancel/expire retain ownership
provenance but make lifecycle terminal. When an invitation is still pending,
the same transaction first takes Distribution's invitation lock, closes it as
`Cancelled` or `Expired`, and appends a closure event, preserving the
claim-before-card lock order.

Ledger owns terminal value movement through
`RecordGiftCardValueReturnAsync`. It locks the exact gift-card value account,
derives current balance from immutable entries, and—when positive—posts a
balanced debit from the card and credit to the funding-root corporate account.
Cancellation and expiration have separate operation types, both linked to the
issuance transaction. A zero balance creates no synthetic posting. Card state,
the lifecycle event with returned amount/transaction link, distribution
closure, Ledger entries, and audit commit atomically. The card-scoped
idempotency event and terminal uniqueness prevent duplicate financial effects.

`gift_cards.lifecycle_events` has forced tenant/platform/exact-owner RLS and an
append-only trigger. A separate trigger rejects every later mutation of a
terminal card; Distribution similarly protects terminal invitation states.
State/financial check constraints keep ownership, transition, returned amount,
currency, and ledger-link combinations coherent.

`GiftCardExpirationWorker` creates a fresh trusted system scope under the
stable `SystemActorIds.GiftCardExpiration` actor. Its bounded processor selects
due non-terminal cards and routes each through the same service and PostgreSQL
locks; retries and multiple instances therefore converge on one terminal event
and at most one return transaction. Poll interval and batch size are validated
configuration. The Development console uses only the public organization,
platform, and cardholder endpoints to demonstrate actions, immutable history,
and returned value.

### Bounded bulk issuance and distribution (IMPL-015)

Distribution owns durable `BulkGiftCardBatch` and append-only
`BulkGiftCardBatchItem` results. A request contains a stable batch reference,
tenant-scoped idempotency key, and 1–100 uniquely referenced items. Each item
contains the normal issuance intent plus exactly one email or E.164 recipient.
The normalized, ordered request forms a canonical intent hash. Deterministic
child keys derived from the batch key, item reference, and operation make
single-card issuance and distribution safe to reuse without a parallel
financial or ownership implementation.

`BulkGiftCardBatchService` requires both the exact-organization issue and
distribute permissions. It persists `Processing`, calls the existing issuance
and prepared-distribution services for every item, records immutable snapshots
in request order, audits one completed batch, and changes the batch to
`Completed` in one outer `SERIALIZABLE` transaction. Any static validation or
business failure rolls back every card, invitation, Ledger posting, batch row,
item row, audit effect, and Notifications-outbox row. Dispatch can observe the
activation message only after commit, so an aborted request cannot emit a
usable link.

`distribution.bulk_batches` and `distribution.bulk_items` use forced tenant
RLS, uniqueness and coherence constraints, source-record foreign keys, a
one-way Processing-to-Completed trigger, an exact completed-count check, and
append-only completed results. A matching retry returns the original batch;
changed normalized intent conflicts. The query contract returns stable-order
items and per-currency totals without exposing recipient contact values or raw
claim secrets. The Development console exercises both POST and GET public
routes and can open each captured activation delivery.

### Durable asynchronous bulk issuance (IMPL-034)

The synchronous 1-100 row path above remains unchanged. A separate async
endpoint accepts 1-2,000 normalized rows, persists the batch and every row's
complete issuance/distribution intent in one transaction, and returns `Pending`
with a durable identifier. Sensitive normalized recipient contact is retained
only for processing; status pages expose the masked form through a strict,
position-based opaque cursor.

`BulkGiftCardBatchWorker` runs bounded passes under the dedicated
`SystemActorIds.BulkGiftCardBatch` identity. Each pass creates a fresh scope and
claims one pending row with `FOR UPDATE SKIP LOCKED`, so multiple API instances
cannot own the same row. The row then reuses the existing issuance, ownership,
Ledger, invitation, audit, and Notifications-outbox paths in one serializable
transaction. Attribution remains the member who accepted the batch while the
actual processing audit actor is the trusted system identity. Deterministic
child keys make a restarted pass converge without a duplicate card, posting,
invitation, or notification. Transient concurrency conflicts leave the row
pending; mapped business failures settle it once with a stable failure code.

Rows transition only from `Pending` to `Succeeded` or `Failed`; database
triggers forbid intent changes, outcome revision, and deletion. Batch counts
advance with row settlement and `Completed` means that every row has an outcome,
not that every row succeeded. Acceptance deliberately does not reserve
corporate credit: rows consume the posted balance progressively, so insufficient
funds is an ordinary durable per-row result.

Retry preserves the completed source as immutable history and creates at most
one child batch containing only its failed rows. The child keeps the original
issuance and distribution keys, and repeated retry requests return that same
child. Forced RLS applies to both the parent/child batches and all persisted
intent rows.

### Financial reporting, reconciliation, and investigation (IMPL-016)

Reporting is a supporting module with contracts and an implementation but no
DbContext, migration history, or private projection store. It performs
parameterized Npgsql reads inside the shared transaction/session-context
boundary. Organization reports require both corporate-credit-view and
gift-card-view authority against the tenant root; platform reports require the
corresponding two platform permissions. A subsidiary-scoped grant cannot widen
into root-wide finance access.

Per-currency summaries and the stable `(occurred_at_utc, event_key)` cursor
timeline are rebuilt from Corporate Credits, Gift Cards, Distribution, and
Ledger records. They cover allocation, reversal, issuance, delivery, claim,
and lifecycle events without copying source data. Recipient queries list only
the current identity owner's cards, derive balances from Ledger entries, and
compose card financial/distribution/lifecycle history without requiring a
company membership. Suspended organizations and inactive or terminal cards
remain historically visible to otherwise authorized callers.

IMPL-018 adds optional organization-history search without creating a
projection or changing the cardholder surface. Category, operation, and
currency are normalized exact matches; the bounded reference input is a
literal case-insensitive match across business and public card references; and
UTC time bounds are inclusive at the start and exclusive at the end. Filters
run after the tenant-scoped union and before deterministic pagination.
Unfiltered callers retain the v1 cursor. Filtered pages use a v2 cursor carrying
a SHA-256 fingerprint of the normalized filter set, and fail safely if that
cursor is reused with different filters.

Reconciliation is deterministic and read-only. It verifies balanced
transactions and entry counts; domain-to-Ledger transaction, amount, currency,
and account-role links; expected versus actual corporate balance; card balance
bounds and terminal zero value; and orphan Phase 2 transactions. Findings are
returned to the caller and never trigger a repair, balance mutation, or
replacement history.

Exact-owner Ledger SELECT policies follow the already protected Gift Cards
ownership row while preserving the existing tenant/platform policies and all
write restrictions. Distribution event SELECT policy now lets the exact
claimant see the complete invitation history, not only events whose actor was
that claimant. Both paths remain forced-RLS and fail closed without verified
session context.

Audit remains the owner of investigation queries. Stable cursor pagination and
operation, outcome, and correlation filters require the new
`organization.audit.view` or `platform.audit.view` permission. The Development
console exposes organization finance/reconciliation and recipient My Cards
workflows exclusively through the public endpoints.

### Independent frontend client boundary (IMPL-017)

The backend, finance/customer portal, and recipient application have separate
repository and release boundaries. This modular monolith remains the only
authority for identity, memberships, permissions, tenant isolation, ownership,
and money. Frontends consume the versioned OpenAPI contract and never infer
authority from hidden UI state.

`GET /api/v1/me` composes the authenticated Identity record with the authority
already verified by bearer authentication. It reports identity, platform, or
organization context. Organization context includes the active membership,
tenant root, selected organization, and permissions evaluated against that
exact selected target. Platform context exposes only permissions resolved from
persisted platform assignments.

`GET /api/v1/me/organizations` is a pre-selection organization-picker query.
It is legal only without `X-Organization-Id` and returns the exact user's active
memberships. SELECT-only PostgreSQL policies admit matching membership and
organization rows only when `app.user_id` matches and no organization is
selected. The policies cannot write, and the normal header-based authentication
path re-verifies a selected membership before scoped work begins.

`GET /api/v1/organizations` provides a bounded root-customer directory for the platform operator
operators with `platform.organizations.view`, including literal
case-insensitive name/code search and exact status filtering.

Production browser deployments use a same-origin BFF/reverse proxy. The backend
stays bearer-only and does not enable broad CORS; the BFF keeps refresh tokens
server-side behind an `HttpOnly`, `Secure` browser session and adds CSRF
protection for state changes. A native client may use bearer tokens directly
only with operating-system secure credential storage. These deployment
controls are defined by ADR-037; they do not add a second authorization model.
For source-IP quotas, a trusted BFF also overwrites `X-Forwarded-For` with its
observed client address and its immediate address is explicitly allowlisted by
the backend. Arbitrary forwarding chains and browser-supplied header values are
not trusted.

### Portal team-administration composition (IMPL-020)

Organization membership remains owned by Organizations, global staff identity
remains owned by Identity, and role authority remains owned by Authorization.
The API composes these narrow contracts without moving authorization or tenant
rules into the client.

Membership creation accepts exactly one existing-user selector. The original
UUID selector remains compatible; the email selector enters Identity's
`IOrganizationStaffDirectory`, which requires
`organization.memberships.create` before normalizing or querying an active
email identity. Missing, disabled, and non-email identities share one
non-enumerating not-found result. No account, password, session, invitation, or
recipient-phone data crosses this boundary.

Membership listing first uses Organizations' existing permission/RLS path,
then Identity independently rechecks organization or platform
membership-view authority before composing nullable staff email. Role
assignment listing stays inside Authorization, requires `role.view`, filters
to the exact organization under forced RLS, and orders by creation time and
UUID. The contract is additive and does not change cardholder endpoints.

### Independent client contract convergence (IMPL-021)

The cardholder claim-session/proxy slice and portal team-administration slice
are published together as one additive backend revision. Identity registers
one scoped `AuthenticationService` instance behind both ordinary
authentication and claim-session issuance; its independently authorized staff
directory remains a separate composition boundary. A served-OpenAPI regression
test requires both client surfaces to coexist. Convergence adds no migration or
new domain behavior and leaves the backend authoritative for identity,
authorization, tenant isolation, ownership, and money.

### Authenticated protected-link sharing (IMPL-022)

Sharing owns `sharing.shares` and append-only `sharing.events`. A 256-bit opaque
link secret and PBKDF2-SHA256 six-digit PIN hash are persisted; raw values are
returned only by successful creation with `Cache-Control: no-store`. A share is
Pending, Claiming, Claimed, Cancelled, Expired, or Locked. Exactly 24-hour
expiry, five failed PIN attempts, immutable creation identity, sender
idempotency, forced RLS, and no runtime DELETE privilege are enforced below the
HTTP layer.

The exact owner creates against Gift Cards' eligibility boundary and Ledger's
locked posted balance. Reporting subtracts Pending/Claiming reservations to
return `reservedBalance` and `availableBalance`; Ledger remains posted-value
authority. Claim requires a different authenticated existing identity and one
parsed `app.share_id` candidate. It atomically records a balanced source debit
and child credit, creates the recipient-owned child card/account, and preserves
immutable source/root/generation lineage. Matching claim replay returns the
same child; concurrent financial races return a safe retryable conflict.

Sender cancellation, bounded background expiry, fifth-attempt lock, and source
cancellation/expiration close pending reservations without Ledger entries.
The public API exposes exact-owner create/list/cancel and authenticated claim;
the Development console consumes those same endpoints. Staff reconciliation
consumes those same authoritative records.

### Verified direct-recipient sharing (IMPL-023)

Direct invitation creation reuses the same exact-owner eligibility, locked
balance, reservation, cancellation, expiry, and source-lifecycle path. Identity
owns email/E.164 normalization and masking. Sharing persists the protected
normalized contact and token hash but returns/audits only the masked form; raw
claim material crosses only the post-commit notification boundary.

`POST /api/v1/share-invitation-claims` is anonymous and source-IP rate limited.
The parsed identifier establishes only one anonymous `app.share_id` candidate;
the secret is verified before terminal state is disclosed. The existing Phase 2
recipient resolver then reuses or creates the identity and the transaction-local
context is promoted before child/Ledger writes. New identities receive an
optional token pair for BFF session establishment; existing identities receive
no session. Serializable races return retryable 409 and replay with the same
idempotency key returns the exact child and transfer.

### POS client and terminal authentication (IMPL-026)

Payments owns `payments.pos_clients` and `payments.pos_terminals`. Both are
platform-scoped rather than tenant-scoped: the platform operator owns the stores, so no
customer organization owns a till and there is no `organization_id` to isolate
on. Registration requires the named `platform.pos.clients.manage` permission and
is audited; the 256-bit client secret is returned once and only its SHA-256 hash
is persisted, behind a check constraint pinning the column to 64 hex characters.

`POST /api/v1/pos/auth/token` exchanges a client code, its secret, and a terminal
code for a 15-minute device token. Unknown clients, wrong secrets, disabled
clients or terminals, and unknown terminals are refused identically, and the
secret is verified even when the client does not exist, so neither the response
body nor its timing reveals which POS codes are registered.

These two tables deliberately carry no Row-Level Security, for the same reason
the platform-role tables do not (ADR-021). The credential exchange must read them
before any caller is authenticated, so a policy requiring verified session
context would make authentication impossible. Immutability triggers instead fix
each row's identity, code, and store reference after registration, so a code
cannot be rewritten to silently repoint every till that authenticates with it,
and neither table grants DELETE to the runtime role.

`IExecutionContext` gains a POS principal that is neither a platform operator,
an identity user, nor a system job. It carries no user, membership,
organization, or tenant scope, so tenant RLS fails closed for it. The API
authentication adapter resolves it *before* the user path, because a POS token
carries no subject and must never be evaluated as one, and refuses outright when
an `X-Organization-Id` header accompanies it rather than ignoring the header, so
a till cannot appear to select a customer. Every other execution-context setter
clears the POS principal, so a terminal identity cannot survive into another
context.

Two limits are recorded rather than left to be discovered: a POS access token
cannot be revoked before its 15-minute lifetime elapses, consistent with the
ADR-028 stance on access tokens, so disabling a client stops new tokens but not
outstanding ones; and there is no secret-rotation operation, so a compromised
secret currently requires re-registering the client.

### QR payment token issuance (IMPL-025)

The first Phase 4 slice. Payments is an independent module with its own
contracts, domain, application service, `payments` schema, DbContext, migration
history, query filter, and enabled/forced RLS. It owns
`payments.payment_tokens` and nothing else yet: provisions (ADR-033) and
redemption (ADR-018) are separate slices, so an issued credential currently just
expires.

A token is 256 opaque CSPRNG bits rendered as `{tokenId:N}.{base64url secret}`,
per ADR-017. It encodes no card, owner, amount, or balance; the identifier
selects a row and the secret proves possession. Only the secret's SHA-256 hex is
persisted, behind a check constraint pinning the column to exactly 64 hex
characters so a raw secret cannot be written into it. Expiry is derived at
issuance from the server clock and a validated 60-second option, so
configuration cannot silently widen the replay window. There is deliberately no
state column: consumption is a timestamp and expiry is clock-derived, which
avoids a second source of truth that could disagree with the clock.

`POST /api/v1/me/gift-cards/{giftCardId}/payment-tokens` requires the exact
identity owner, responds `no-store`, and returns the raw credential once. The
service runs at `SERIALIZABLE` so the eligibility read and the token write
cannot straddle a lifecycle change, and records membership-free identity audit
that never contains the credential.

Gift Cards alone decides whether a card may be spent from, through the narrow
`IGiftCardPaymentWriter` boundary. Its `EnsureSpendable` check is deliberately
distinct from `EnsureShareEligible`: sharing additionally requires the card to be
transferable and divisible, but ADR-030 defaults both to `false`, so reusing that
check would have made an ordinarily issued card impossible to pay with. Those
policies govern splitting value, not spending it. Ownership is verified before
eligibility so a stranger receives the same not-found result as for a card that
does not exist.

Beyond forced tenant/owner RLS, two triggers enforce invariants the application
alone should not hold: token identity, binding, secret hash, and validity window
are immutable after issuance — otherwise widening `expires_at_utc` would silently
extend a replay window — and once `consumed_at_utc` is set it can never be
cleared or moved, so single use survives a defect in the future redemption path.
The runtime role has SELECT, INSERT, and UPDATE but no DELETE: a spent or expired
credential is evidence for reconciliation.

### POS payment provisions (IMPL-027)

`payments.payment_provisions` holds card value for a sale in progress, in the
`Active`, `Confirmed`, `Cancelled`, and `Expired` states of ADR-033. The window
is the two minutes accepted as ADR-044, derived at creation from the server
clock and a validated option, so an environment cannot silently widen it. There
is no posted Ledger movement anywhere in this slice: a hold reserves value,
confirmation is IMPL-028, and cancellation and expiry release it with no
financial effect at all.

Creation runs at `SERIALIZABLE`. It parses the presented credential, takes a
credential-scoped advisory lock, then consumes the credential exactly once
inside that transaction, so two tills scanning the same code serialise and the
unique index on `payment_token_id` is the backstop. Malformed, unknown,
replayed, and expired credentials are refused identically, and the secret is
verified even when no row exists, so neither the response nor its timing
separates the cases.

Availability is posted balance minus **every** active hold of either kind.
Payments subtracts Sharing's pending reservations before reserving, Sharing
subtracts active provisions at both of its create paths, and Reporting's
owned-card reads report the sum of both as reserved value. Counting only one
kind is how a share and a till end up spending the same money
(`DOMAIN_RULES` §10.20). Payments and Sharing reference only each other's
`.Contracts` project, so the two-way dependency is not a project cycle;
`PaymentReservationQuery` is a separate implementation type so the container
graph stays acyclic too.

A POS principal holds no user, membership, organization, or tenant scope, so
tenant RLS correctly hides everything from it. Each read it legitimately needs
is opened by its own narrow candidate policy keyed on the transaction-local
`app.payment_token_id`: the credential row in `payments`, the card in
`gift_cards`, its value account and entries in `ledger`, and its share
reservations in `sharing`. All four are SELECT-only, and the 256-bit secret is
still verified in constant time before value moves, so the identifier alone
grants visibility and never authority. The Sharing reservation read is
deliberately unfiltered at the EF layer for a specific reason: it is a sum, so a
hidden row would return a smaller reserved figure rather than failing closed.

Two writes needed the same treatment. Provision rows carry a POS-client
candidate so one till can read and cancel only its own holds. Audit rows carry a
real organization scope — the card's funding tenant, so staff can see holds
against their customers' cards — admitted by an INSERT-only policy requiring
that the calling POS client actually holds a provision in that tenant. That one
predicate covers creation and cancellation without admitting anything else, and
grants no read access to audit history.

Database triggers hold what the application alone should not: a hold's
credential, card, owner, terminal, amount, and window are immutable after
creation, so a hold cannot be repointed or silently extended past its ADR-044
window; and `Active` is the only state a provision may leave, so a released hold
can never be revived to reserve value twice. `PaymentProvisionExpirationWorker`
settles elapsed holds under the stable `SystemActorIds.PaymentProvisionExpiration`
actor, but a provision already stops reserving value at its deadline because
availability is clock-derived, so a late sweep cannot strand a cardholder's
value in the meantime.

### Redemption confirmation (IMPL-028)

Confirmation first resolves the calling POS client's own provision, then copies
its server-owned payment-token identity into the transaction-local execution
context. One `SERIALIZABLE` transaction re-locks that provision, revalidates the
card through Gift Cards, takes the common card/value advisory locks, posts
`gift_card.redemption`, changes the provision to `Confirmed`, and appends a
POS-client audit record. The request states a positive amount no greater than
the held ceiling; posting less releases the remainder because only `Active`
provisions count as reserved.

Redemption debits `GiftCardValue` and credits one platform-scoped
`PlatformRedemptionSettlement` account per currency (ADR-045). The Ledger key
is derived solely from the payment-token UUID, so a same-intent retry returns
the original transaction without a time window while changed intent conflicts
(ADR-018, ADR-046). A transaction-local, server-generated Ledger transaction
candidate admits exactly its two entry inserts without creating an RLS cycle.
The POS/payment limiter partitions by authenticated user or POS client rather
than source IP, so multiple tills behind one store gateway do not share a
single quota.

Reporting adds per-currency `Spent`, organization redemption events, and the
existing owned-card Ledger timeline naturally exposes the debit. Reconciliation
links confirmed provisions to the immutable posting and, for a platform
operator, compares the global settlement balance with all confirmed
redemptions; it reports divergence and never repairs it.

### Redemption refunds (IMPL-029)

`POST /api/v1/pos/payment-provisions/{provisionId}/refunds` appends a positive
partial refund to a confirmed sale. Only the POS client that created the sale
may call it, although any authenticated terminal under that client may do so.
Each caller key is unique within the provision and gives exact-intent retries
the original response; changed intent conflicts. It is not the financial safety
boundary: the provision advisory lock and a database trigger serialize the
immutable refund sum and refuse a cumulative amount above the confirmed charge
(ADR-047).

Gift Cards alone decides refund lifecycle eligibility. Identity-owned `Active`
and `Suspended` cards may receive value; terminal `Cancelled` and `Expired`
cards cannot, preserving their zero-balance invariant. A full reversal in this
slice is a refund of the remaining amount. Platform correction authority and
alternate payouts remain deliberately separate (ADR-048).

Ledger posts `gift_card.refund` by debiting the per-currency
`PlatformRedemptionSettlement` account and crediting the original
`GiftCardValue` account. The original provision and redemption never change.
Refund records and entries are append-only, use the same card/value/settlement
lock order as confirmation, and are admitted through exact POS, payment-token,
refund, and Ledger transaction candidates under forced RLS. Reporting preserves
gross `Spent` and adds `Refunded` and `NetSpent`; organization and owner history
show the compensating event, while reconciliation rebuilds settlement as gross
redemptions minus refunds and never repairs divergence.

### POS payment reporting (IMPL-030)

`GET /api/v1/platform/reports/payments` is a PostgreSQL-backed read model over
authoritative provision and immutable refund rows. It requires the dedicated
`platform.payments.view` permission; customer organization users, cardholders,
POS principals, and platform operators without that permission are refused
before a report transaction begins. The report intentionally owns no DbContext,
schema, projection, or write path.

One receipt-centered row retains the original client, terminal, store,
transaction reference, tenant, public card reference, provision, confirmation,
refund, net, state, timestamp, and Ledger-link attribution. Filters are bounded,
parameterized, and normalized; literal reference patterns escape PostgreSQL
wildcards, UTC bounds are inclusive/exclusive, and cursors bind the normalized
filter fingerprint to `(created_at_utc, provision_id)`. Amount totals never mix
currencies. A fully reversed payment is derived only when the immutable refund
sum equals the confirmed amount.

`GET /api/v1/platform/reports/payments/{paymentProvisionId}` returns the same
receipt view plus ordered immutable refund lines with the terminal/store that
performed each return. It deliberately omits owner identity, credential data,
POS secrets, and refund idempotency keys. PostgreSQL RLS remains authoritative;
Elasticsearch and materialized projections stay deferred until measured volume
or latency justifies their consistency and recovery cost (ADR-049).

### Numeric payment credential (IMPL-031)

Payment-token issuance returns a CSPRNG 12-digit numeric alias alongside the
opaque QR credential. They bind to one token row, expiry, and irreversible
consumption stamp. Only SHA-256 of the canonical digits persists; a global
partial unique index makes lookup unambiguous, and conflict-safe insertion
retries a random collision without reading another tenant's row.

The POS request supplies exactly one of `paymentToken` or `paymentCode`.
Server-side normalization accepts ASCII digits with spaces or hyphens, derives
the hash, and establishes a transaction-local exact-hash SELECT candidate.
After resolving the server-owned token UUID, processing returns to the existing
exact-ID RLS and advisory-lock path. The numeric candidate grants no write and
no visibility of unrelated tokens, cards, owners, balances, or tenants.
Malformed, unknown, expired, consumed, and concurrent replay paths collapse to
the same response. A serializable loser is translated at the service boundary
rather than leaking a database conflict as HTTP 500 (ADR-050).

### Sharing reporting and reconciliation (IMPL-024)

`GET /api/v1/me/shares` keeps Sharing as owner of sender/recipient history and
adds bounded kind/state/direction filters whose cursors are bound to the filter
set. Gift Cards supplies public references only for cards visible through its
own forced RLS policy; Sharing does not copy card display data or widen access.

Reporting adds masked Sharing events to organization and owned-card timelines.
Root issuance remains the corporate-funding event; shared child creation is
represented by `gift_card.share_transfer`, preventing value from being counted
twice. Organization reconciliation is read-only and deterministically checks
active reservations, persistent Claiming anomalies, claimed transfer effects,
child source/root/generation/owner/value lineage, and orphan Ledger operations.
Existing paired corporate-credit and gift-card view permissions authorize this
staff read model; no repair command or broader permission is introduced.
