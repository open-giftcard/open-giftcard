# Architecture and Domain Decisions

## Document Status

This file records durable decisions and unresolved architecture-blocking questions.

Decision statuses:

* Accepted
* Proposed
* Open
* Superseded
* Rejected

Do not treat an Open or Proposed item as accepted.

Some entries cite `REVIEW-001`, an internal critical review carried out on
2026-07-23. That review is not published; the citation records where a decision
came from, and the decision itself is stated in full here.

---

## Decision Index

| ID      | Decision                                            | Status   | Blocks                   |
| ------- | --------------------------------------------------- | -------- | ------------------------ |
| ADR-001 | Use a modular monolith                              | Accepted | —                        |
| ADR-002 | PostgreSQL is the primary source of truth           | Accepted | —                        |
| ADR-003 | Enforce immutable ledger-first financial operations | Accepted | Sprint 2                 |
| ADR-004 | Module boundary and assembly strategy               | Accepted | Sprint 1 scaffolding     |
| ADR-005 | Tenant-isolation mechanism                          | Accepted | Organization persistence |
| ADR-006 | Hierarchy-aware authorization scope                 | Accepted | Authorization schema     |
| ADR-007 | Global user plus organization membership model      | Accepted | —                        |
| ADR-008 | Append-only audit storage                           | Accepted | Audit implementation     |
| ADR-009 | Refresh-token rotation and reuse detection          | Accepted | Authentication           |
| ADR-010 | Maximum organization hierarchy depth                | Accepted | Subsidiary creation      |
| ADR-011 | Cross-module communication mechanism                | Accepted | Module scaffolding       |
| ADR-012 | Public identifier strategy                          | Accepted | Database scaffolding     |
| ADR-013 | Audit tamper-evidence strategy                      | Accepted | IMPL-032                 |
| ADR-014 | Ledger accounting representation                    | Accepted | Sprint 2                 |
| ADR-015 | Share-value reservation timing                      | Accepted | Sharing                  |
| ADR-016 | Share-link protection policy                        | Accepted | Sharing                  |
| ADR-017 | Dynamic QR token design                             | Accepted | Phase 4 QR implementation |
| ADR-018 | Redemption idempotency-key derivation               | Accepted | Phase 4 redemption       |
| ADR-019 | PostgreSQL database roles                            | Accepted | Sprint 1 persistence     |
| ADR-020 | Execution-context and RLS propagation               | Accepted | Tenant enforcement       |
| ADR-021 | Platform scope versus customer organization hierarchy | Accepted | Organization schema    |
| ADR-022 | Initial test strategy                               | Accepted | Test scaffolding         |
| ADR-023 | Tenant-boundary RLS on the organizations table       | Accepted | Organizations tenant isolation |
| ADR-024 | Organization code uniqueness scope                   | Accepted | Subsidiary creation (security) |
| ADR-025 | Auditing denied and failed operations               | Accepted | Security monitoring      |
| ADR-026 | Transaction nesting and isolation for financial ops | Accepted | Sprint 2 / Ledger        |
| ADR-027 | Public API versioning strategy                      | Accepted | First external client    |
| ADR-028 | Identity password and token-session baseline         | Accepted | Identity implementation  |
| ADR-029 | One-time platform-administrator bootstrap            | Accepted | Phase 1 bootstrap        |
| ADR-030 | Phase 2 gift-card funding, delivery, and lifecycle baseline | Accepted | IMPL-012 through IMPL-015 |
| ADR-031 | Tenant root versus active operational organization   | Accepted | Phase 2 prerequisite     |
| ADR-032 | Tenant-isolated audit and membership attribution     | Accepted | Phase 2 prerequisite     |
| ADR-033 | First-class payment provision lifecycle              | Accepted | Phase 4 payments         |
| ADR-034 | Staff and recipient login/delivery identifiers       | Accepted | IMPL-013                 |
| ADR-035 | Gift-card lifecycle authority and terminal-state policy | Accepted | IMPL-014              |
| ADR-036 | Phase 2 read-only reporting and reconciliation boundary | Accepted | IMPL-016              |
| ADR-037 | Independent frontend client and browser-security boundary | Accepted | IMPL-017 and frontend repositories |
| ADR-038 | Authoritative additive financial-history search          | Accepted | IMPL-018 / PORTAL-010 |
| ADR-039 | Cardholder claim session and trusted proxy boundary      | Accepted | IMPL-019 / CARD-001   |
| ADR-040 | Portal-safe team administration contract                 | Accepted | IMPL-020 / PORTAL-012 |
| ADR-041 | Independent client contract convergence                  | Accepted | IMPL-021              |
| ADR-042 | Synchronized Phase 2 release-candidate identity          | Accepted | RELEASE-001           |
| ADR-043 | POS client boundary                                      | Accepted | Phase 4 POS repository |
| ADR-044 | Payment-provision reservation window                     | Accepted | IMPL-027 provisions   |
| ADR-045 | Redemption counter-account and settlement representation | Accepted | IMPL-028 redemption   |
| ADR-046 | Confirmation amount and terminal retry window            | Accepted | IMPL-028 redemption   |
| ADR-047 | Partial-refund accumulation and over-refund protection    | Accepted | IMPL-029 refunds      |
| ADR-048 | Refund lifecycle and reversal authority                   | Accepted | IMPL-029 refunds      |
| ADR-049 | POS payment reporting authority and source                | Accepted | IMPL-030 reporting    |
| ADR-050 | Human-enterable numeric payment credential                | Accepted | IMPL-031 payment code |
| ADR-051 | Asynchronous bulk gift-card batches                       | Accepted | IMPL-034 bulk upload   |
| ADR-052 | Organization card visibility after claim                  | Accepted | IMPL-033 card register |
| ADR-053 | E-pin reseller partners as tenant-owned minting clients   | Accepted | PARTNER-001 registry  |
| ADR-054 | Partial approval is opt-in per request                    | Accepted | POS partial approval  |
| ADR-055 | A POS device proves its audit scope with the card         | Accepted | POS balance inquiry   |
| ADR-056 | Taking a hold requires an idempotency key                 | Accepted | POS provision create  |
| ADR-057 | Audit checkpoint custody is an explicit provider choice   | Accepted | Deployment candidate  |

---

## ADR-001 — Use a Modular Monolith

**Status:** Accepted

### Context

The platform requires strict consistency for organization, authorization, and
financial operations.

The initial project scope and team size do not justify the deployment and
operational complexity of microservices.

### Decision

Build the platform as a modular monolith with explicit business-module boundaries.

### Consequences

* A single initial deployment
* Simpler database transactions
* Simpler local development
* Lower operational overhead
* Module boundaries must be actively enforced
* Future service extraction remains possible only if modules do not share internal implementation details

---

## ADR-002 — PostgreSQL as Primary Source of Truth

**Status:** Accepted

### Decision

PostgreSQL is authoritative for business ownership, authorization relationships,
financial records, and final transaction outcomes.

Redis and Elasticsearch are supporting data stores only.

### Consequences

* Redis may store short-lived or cached state.
* Elasticsearch may store searchable projections.
* Neither may independently determine authoritative balance or ownership.
* Derived stores must be recoverable or reconciliable from PostgreSQL.

---

## ADR-003 — Ledger-First Financial Operations

**Status:** Accepted

### Decision

Every value-changing operation must produce immutable ledger entries.

Direct balance mutation must not be the sole financial record.

The Ledger module must be introduced together with the first corporate-credit
and gift-card financial use cases.

### Consequences

* Financial history remains reconstructable.
* Corrections require compensating transactions.
* Idempotency and reconciliation must be designed early.
* Balance projections may be introduced for performance but remain derived state.

---

## ADR-004 — Module Boundary and Assembly Strategy

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Sprint 1 solution scaffolding

### Decision

Adopt **Option B (refined)**: one project per business module, each exposing a
small `.Contracts` project for its public surface.

Physical layout accepted for Sprint 1:

* One project per business module, containing internal `Domain`, `Application`,
  and `Infrastructure` folders.
* One small `.Contracts` project per business module holding only that module's
  public interfaces and DTOs.
* One API / Host project.
* One small `BuildingBlocks` project for genuinely shared technical primitives.

Initial modules only:

* Identity
* Organizations
* Authorization
* Audit

No other module (Ledger, Corporate Credits, Gift Cards, Distribution, Sharing,
Payments, Redemption, Reporting, Notifications) may be scaffolded until it
enters the current task scope.

Module implementation types must remain `internal` wherever possible. Another
module may reference only the owning module's `.Contracts` project — never its
implementation assembly.

### Rationale

Option B gives real compile-time isolation between modules — the boundary that
matters most for a modular monolith — at a moderate project count. Intra-module
layering is enforced by folder convention plus architecture tests (see ADR-022)
rather than additional projects.

### Options Considered

* **Option A — project per module per layer:** strongest compile-time
  boundaries but roughly 4× project count; over-architected for this scope.
* **Option B — project per module (+ `.Contracts`):** *accepted.*
* **Option C — one project with module folders:** minimal scaffolding but no
  compile-time boundary; accidental coupling is nearly certain and undermines
  the modular-monolith premise.

### Consequences

* Cross-module references are limited to `.Contracts` assemblies.
* Intra-module layer rules rely on architecture tests, not the compiler.
* Future service extraction remains feasible because modules share no internal
  implementation types.
* Migrating away from Option C's accidental coupling is avoided by starting at
  Option B; moving B → A later is mechanical if ever required.

### Superseded Options

### Option A — Separate project per module and layer

Example:

```text
Modules.Identity.Domain
Modules.Identity.Application
Modules.Identity.Infrastructure
Modules.Identity.Contracts
```

Advantages:

* Strong compile-time boundaries
* Explicit dependencies
* Easier future extraction

Costs:

* High project count
* More scaffolding
* Potential over-architecture for the internship scope

### Option B — Separate project per module

Example:

```text
Modules.Identity
Modules.Organizations
Modules.Authorization
Modules.Audit
```

Each project contains internal domain, application, and infrastructure folders.

Advantages:

* Useful compile-time boundaries
* Lower project count
* Appropriate for a medium-sized modular monolith

Costs:

* Internal layer boundaries rely partly on convention and architecture tests

### Option C — One application project with module folders

Advantages:

* Minimal scaffolding
* Fast early development

Costs:

* Weak compile-time boundaries
* Easier accidental coupling
* Harder future extraction

### Decision Outcome

Option B accepted. See the **Decision** section at the top of this ADR.

---

## ADR-005 — Tenant-Isolation Mechanism

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Organization persistence

### Decision

Use a **shared PostgreSQL database with a separate schema per module**, and
enforce tenant isolation through `organization_id` plus PostgreSQL Row-Level
Security (RLS) as the authoritative database-level barrier.

Accepted mechanism:

* One shared PostgreSQL database.
* A separate schema per module (`identity`, `organizations`, `authorization`,
  `audit`) — module ownership of tables (see ADR-004) is a **separate concern**
  from tenant isolation.
* `organization_id` on every tenant-owned table.
* EF Core query filters for developer ergonomics and defense in depth.
* PostgreSQL RLS as the authoritative database-level isolation barrier.

Application-level filtering alone is **not** sufficient; RLS is the enforced
defense against a missed filter.

### Entity Tenancy Categories

Not every table is tenant-owned. Each entity must be classified as:

* **Global** — no `organization_id` (e.g. global user accounts, global
  permission definitions).
* **Platform-scoped** — owned by the platform scope, not a customer
  organization (see ADR-021).
* **Tenant-scoped** — carries `organization_id` and is subject to RLS (e.g.
  organizations, organization memberships, organization roles, role
  assignments, and all tenant-owned business records).

The tenancy category of each Sprint 1 entity must be documented in
`docs/ARCHITECTURE.md`.

### Runtime Database Role Requirement

The runtime application database role must be non-superuser, must not hold
`BYPASSRLS`, must be subject to RLS, and must hold only the privileges the
application requires. Platform-level cross-tenant access must use an explicit,
controlled execution context and RLS policy path — never a superuser connection
and never by disabling RLS. See ADR-019 and ADR-020.

### Options Considered

* **Shared DB + schema-per-module + `organization_id` + RLS:** *accepted.*
* **Shared DB with application filtering only:** rejected — no defense against a
  forgotten filter.
* **Schema per tenant / database per tenant:** rejected — unworkable migrations
  and connection management at thousands of organizations, and cross-tenant
  platform operations and reporting become hard.

### Consequences

* Every tenant-owned table gains an `organization_id` and an RLS policy from day
  one; retrofitting isolation later is avoided.
* RLS requires a per-transaction session-context mechanism (see ADR-020) and a
  disciplined database-role model (see ADR-019).
* A missed EF Core query filter cannot leak cross-tenant data.
* The isolation model (shared vs per-schema vs per-database) is fixed early
  because changing it later is a full data migration.

---

## ADR-006 — Hierarchy-Aware Authorization Scope

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Authorization schema

### Decision

Use an **assignment-scoped** authorization model. Scope is stored on the
membership-role assignment, not on the role, so the same role is reusable with
different scopes.

Accepted schema shape:

* **MembershipRoleAssignment**
  * `MembershipId`
  * `RoleId`
  * `ScopeType`
  * `AnchorOrganizationId` (when applicable)
* **MembershipRoleAssignmentScopes** (for `SelectedOrganizations`)
  * `MembershipRoleAssignmentId`
  * `OrganizationId`

Supported scope types:

* `Organization` — the anchor organization only.
* `Subtree` — the anchor organization and all descendants (via the ltree path,
  see ADR-010).
* `SelectedOrganizations` — one or more explicitly granted organizations listed
  in `MembershipRoleAssignmentScopes`.

`SelectedOrganizations` must not be represented by a single optional
organization identifier — it is a genuine one-to-many relation.

### Platform-Global Authorization

Platform-global authorization must **not** be represented as an
organization-membership role assignment. A separate platform-role assignment
model is used for platform operators (see ADR-021). Only authorized
platform operators may receive platform-global permissions, and being a
platform operator must not automatically imply every platform permission.

### Effective Authorization Evaluation

Effective authorization evaluates:

* The authenticated global user
* The active organization membership
* Assigned organization roles
* The assignment scope
* The target organization
* The organization hierarchy path

Parent-organization ownership alone never grants access.

### Options Considered

* **Scope on the membership-role assignment (+ separate selected-scope table):**
  *accepted.*
* **Scope on the role itself:** rejected — roles should be reusable across
  scopes.
* **Single optional organization id for selected subsidiaries:** rejected —
  cannot express a multi-organization selected scope.
* **Policy-generated descendant access with no stored scope:** rejected —
  implicit and hard to audit.

### Consequences

* The authorization schema carries a scope dimension from Sprint 1 even if only
  `Organization` and platform scope are exercised initially; adding it later
  would force a migration plus evaluator rewrite plus re-authentication.
* Authorization evaluation depends on the ltree hierarchy path (ADR-010).
* Platform and customer authorization are represented by distinct assignment
  models.

---

## ADR-007 — Global User and Organization Membership

**Status:** Accepted

### Decision

A user account is global.

Organization-specific access is represented through organization memberships.

Roles are assigned to memberships instead of directly to the global user.

### Consequences

* A user may belong to multiple organizations.
* A user may have different permissions in each organization.
* Global account status and membership status are separate.
* Authorization requires an active membership context.

---

## ADR-008 — Append-Only Audit Storage

**Status:** Accepted

### Decision

Audit records are append-only.

The application database role must not receive update or delete privileges on
committed audit records.

### Open Subdecision

Whether audit entries use hash chaining or another tamper-evidence mechanism
remains unresolved under ADR-013.

---

## ADR-009 — Refresh-Token Rotation and Reuse Detection

**Status:** Accepted

### Decision

Successful token refresh rotates the refresh token.

Presenting an invalidated token from the same token family is treated as a
possible compromise and revokes the associated family or session.

### Consequences

* Token-family identity must be stored.
* Token state and replacement relationships must be represented.
* User and membership disabling must revoke relevant sessions.

---

## ADR-010 — Maximum Organization Hierarchy Depth

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Subsidiary creation

### Decision

* The maximum initial customer-organization hierarchy depth is **5 levels**.
* The platform scope is not counted as one of those five levels (see
  ADR-021).
* The depth limit must be configurable but enforced server-side at subsidiary
  creation.
* Cyclic organization relationships are forbidden.

### Hierarchy Representation

* `parent_organization_id` self-reference.
* A materialized hierarchy path using the PostgreSQL `ltree` extension.
* A stored depth value where useful for validation and querying.
* Reparenting must update all descendant paths atomically.

### `ltree` Availability Fallback

If the `ltree` extension cannot be enabled in the target environment, the
recorded fallback is an adjacency list plus recursive CTE traversal. The model
must not silently change — the fallback must be documented as a deliberate
decision.

### Options Considered

* **`parent_id` + `ltree` materialized path, depth cap 5:** *accepted.*
* **Adjacency list only:** simpler writes but recursive CTEs on every scope
  check; retained only as the ltree fallback.
* **Closure table:** fast reads, heavier writes and an extra table — not needed
  at this scale.
* **Unlimited depth:** rejected — forbidden as a default.

### Consequences

* The hierarchy representation is fixed early because it is woven into every
  authorization (ADR-006) and reporting query; changing it later requires a
  backfill.
* The depth *limit* is configurable and cheap to raise later; a conservative
  default of 5 is chosen now.
* Reparenting requires atomic descendant-path maintenance.

---

## ADR-011 — Cross-Module Communication and Atomicity

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Module scaffolding

### Decision

* Use **synchronous public contract interfaces** for request/response
  communication between modules.
* Use **synchronous in-process integration events** where decoupling is useful,
  including audit notifications.
* Fire-and-forget events are **not** permitted in Sprint 1.

### Cross-Module Atomicity

For administrative operations that require an audit record, the business change
and the audit insert must commit **atomically**; an audit failure must fail and
roll back the administrative operation.

Use an explicit **transaction coordinator** capable of sharing the same physical
Npgsql connection and PostgreSQL transaction across the participating module
DbContexts. The transaction boundary must be explicit, visible, and testable.

* Do **not** rely on ambient `TransactionScope` as the default implementation.
* Do **not** introduce a message broker or transactional outbox in Sprint 1.
* The outbox pattern may be introduced later for external or asynchronous side
  effects.

### Forbidden Regardless of Choice

* Direct modification of another module's entities.
* Direct use of another module's internal DbContext.
* Hidden coupling through shared mutable database models.

### Options Considered

* **Synchronous contracts + in-process events + explicit transaction
  coordinator:** *accepted.*
* **Transactional outbox for audit:** rejected for Sprint 1 as premature for a
  single process; may return for external side effects.
* **Eventual-consistency (fire-and-forget) events for audit:** rejected —
  violates the atomicity requirement for audited administrative operations.
* **Ambient `TransactionScope` as default:** rejected — behavior is less visible
  and harder to test than an explicit boundary.

### Consequences

* Modules keep separate DbContexts (ADR-004) but can share one physical
  transaction for atomic multi-module operations.
* A small unit-of-work / transaction-coordinator convention is required at
  scaffolding time.
* Audit is committed in the same transaction as the audited action.

---

## ADR-012 — Public Identifier Strategy

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Initial database scaffolding

### Decision

* Use **UUID v7** for internal primary keys (time-ordered for B-tree index
  locality, non-sequential to avoid enumeration).
* Expose the same identifier through APIs unless a separate human-facing
  reference is required.
* Use separate short or formatted identifiers only for concepts such as
  organization codes, gift card references, commercial references, POS
  transaction references, and financial transaction display references.
* Sequential numeric identifiers must not be used as public credentials or
  access tokens.

### Options Considered

* **UUID v7 PK, exposed as the public id:** *accepted.*
* **UUID v4:** rejected — index fragmentation at scale.
* **Bigint PK + separate uuid public id:** rejected — a double-column burden
  everywhere, only justified at extreme scale.
* **Sequential public identifiers:** rejected — enumeration risk.

### Consequences

* PK type is fixed at scaffolding because changing it after foreign keys exist
  is effectively a schema rebuild.
* Most entities need only one identifier column.
* Human-facing codes are introduced per concept as those features arrive.

---

## ADR-013 — Audit Tamper-Evidence Strategy

**Status:** Accepted
**Date:** 2026-08-06
**Decision:** Signed batch Merkle checkpoints with an external immutable witness

Use **periodic signed checkpoints**, rather than per-row hash chaining.

Hash chaining is the reflex answer but fits this system badly. Each row would
need its predecessor's hash, forcing a global order on audit inserts, and that
fights two deliberate properties of the current design. Audit rows commit inside
the business transaction (ADR-011), so concurrent audited operations write
concurrently; a chain would serialise every audited action behind one lock.
Worse, denial audit writes on a separate connection that commits independently
(ADR-025) precisely so it survives a rollback — its position in any chain is
genuinely undefined, because it may commit before a business audit row created
earlier.

Sealing bounded batches avoids both problems. Every audit row receives a
database-assigned sequence after taking a transaction-scoped shared advisory
lock. The sealer briefly takes the corresponding exclusive lock, selects the
next committed sequence range, computes an RFC 9162-style SHA-256 Merkle root,
and signs a versioned manifest chained to the prior manifest digest. Normal
writers share the lock and therefore remain concurrent with one another.

**The decisive part is key custody, not the hashing scheme.** Append-only grants
already prevent the *application* from editing audit (ADR-019). Tamper-evidence
only adds value against an actor with database-level access — a DBA, a
compromised migration role, or disk access. The signing key must therefore live
somewhere that actor cannot reach: a managed KMS or HSM with a non-exportable
ECDSA P-256 key. A key stored in the same database, or in configuration beside
the connection string, lets the same attacker forge checkpoints and buys
nothing. Algorithm, key identity/version, public key, signature, boundary,
Merkle root, and prior digest are part of the append-only checkpoint.

A database attacker could otherwise delete the checkpoint together with the
rows. The exact signed manifest must therefore be published to external WORM
storage through a provider-neutral, idempotent witness contract. The database
stores a separate append-only receipt only after publication succeeds.

Signing and publication are asynchronous hardening controls, never part of a
financial transaction. An outage raises an operational alert and delays the
checkpoint; it does not refuse business writes. Five minutes and 10,000 records
are bounded defaults rather than permanent domain rules. Rows after the latest
sealed boundary remain temporarily unprotected, which is the accepted bounded
exposure.

Development and tests may use explicit local adapters. Non-Development must
never silently fall back to a local key or mutable witness. Selecting and
provisioning the concrete KMS/HSM, WORM service, and retention duration remains
a deployment decision.

---

## ADR-014 — Ledger Accounting Representation

**Status:** Accepted
**Date:** 2026-07-27

### Context

Phase 2 begins with corporate-credit allocation and later adds gift-card
issuance, distribution, sharing, redemption, refunds, expiration, and reversal.
A representation is required that prevents value from being silently created or
destroyed, preserves history, and supports reconciliation without making a
mutable balance authoritative.

### Decision

Use a **balanced double-entry product ledger** with three core records:

* A ledger account is one pool of value in exactly one currency.
* A ledger transaction is the immutable identity of one posted financial
  operation.
* A ledger entry is one positive debit or credit posting from that transaction
  to one account.

Every posted transaction has at least one debit and one credit, and total debits
must equal total credits independently for every currency. An account's balance
is derived as credits minus debits; any materialized balance remains a
rebuildable projection. Accounts that represent spendable customer or gift-card
value may not end below zero.

The first implementation is **posted-only**. Pending/reserved money is not
introduced until a concrete business flow needs it. Posted transactions and
entries are immutable; corrections and reversals create a new transaction with
opposite postings and a reference to the original.

Each financial transaction has an operation type and an operation-scoped
idempotency key protected by a database unique constraint. Repeating the same
key and payload returns the original result; reusing it with different financial
intent is a conflict.

Financial operations begin at `SERIALIZABLE` (ADR-026). A serialization failure
is returned as a retryable conflict; the caller can safely retry with the same
idempotency key. Automatic retry policy is deferred until measured operational
needs justify one.

### Consequences

* Corporate allocation transfers value from an explicit platform funding account to
  the recipient organization's corporate-credit account; it never edits a
  balance column.
* Gift-card issuance and later movements use the same transaction and entry
  primitives rather than inventing separate balance histories.
* Balancing, positive amounts, currencies, account ownership, idempotency, and
  immutability require both domain validation and PostgreSQL constraints or
  privileges.
* Multi-currency exchange is not implied by balancing. A future exchange
  operation must explicitly model each currency leg and its rate.
* Pending authorization and reservation semantics remain deliberately deferred.

---

## ADR-015 — Share-Value Reservation Timing

**Status:** Accepted
**Date:** 2026-08-02
**Blocks:** Sharing

**Decision:**

* Creating a pending share reserves its exact amount immediately.
* The Ledger remains the authority for posted value. The Sharing module owns
  active share reservations; available card value is the Ledger-derived posted
  balance minus active Sharing reservations.
* Successful claim consumes the reservation and posts one immutable, balanced
  source-card-to-child-card transfer in the same transaction as the claim and
  child-card lineage.
* Cancellation, expiration, and terminal lock release the reservation without
  posting a financial transaction.
* Creation, cancellation, expiry, and claim lock the relevant card/value scope
  and are idempotent so spending and concurrent claims cannot over-consume the
  source.
* A pending share is visible as reserved value; it is never presented as a
  completed transfer.

**Consequences:**

* Sharing adds the first concrete reservation source. Phase 4 payment
  provisions remain separate and must compose with, not reinterpret, active
  share reservations.
* Reservation rows are mutable only through their constrained lifecycle;
  posted ledger history remains append-only.

---

## ADR-016 — Share-Link Protection Policy

**Status:** Accepted
**Date:** 2026-08-02
**Blocks:** Sharing

**Decision:**

* Every generic share link requires a six-digit numeric PIN; there is no
  unprotected value threshold.
* Generic links expire 24 hours after creation, are single-use, and may be
  cancelled only before a successful claim.
* Five failed PIN attempts permanently lock the share. Lock is terminal and
  releases its reservation; no timed retry window is provided.
* Persist only cryptographic hashes of the 256-bit link secret and PIN. Return
  raw generic-link credentials once at creation, never log them, and never
  claim on GET or link-preview traffic.
* Direct email/phone invitations reuse the contact-bound, accountless Phase 2
  activation mechanism rather than weakening generic-link protection. A
  generic link is claimed into an authenticated existing recipient identity;
  a new recipient is created only through the verified contact-bound flow.
* PIN is the only v1 generic-link protection method. Password and pattern
  protection are deferred until a demonstrated product need exists.

**Consequences:**

* The cardholder BFF must consume a presented link secret server-side, redirect
  to a clean URL, and submit the PIN through a CSRF-protected POST.
* Responses and audit records expose only masked recipients and non-secret
  identifiers; raw link credentials never reappear after creation.

---

## ADR-017 — Dynamic QR Token Design

**Status:** Accepted
**Date:** 2026-08-03
**Blocks:** Phase 4 QR implementation

### Context

A cardholder shows a short-lived credential at a till. The credential must
identify which card is paying without becoming a bearer instrument for the
card's value, and the platform must be able to follow it back to the owning
identity and the Ledger-derived balance in order to authorize a payment.

Two shapes were possible. A self-contained signed token (JWT) carries its claims
in the code itself; an opaque token carries only randomness and is resolved by
server-side lookup.

### Decision

**An opaque, server-resolved, single-use token held in PostgreSQL.**

* **Opaque, not JWT.** The credential is 256 bits from a CSPRNG, encoded for
  display as a QR code or a numeric code. It encodes no card id, no owner, no
  amount, and — explicitly — **no balance** (DOMAIN_RULES §10.2). A code on a
  phone screen can be photographed or shoulder-surfed, and anything embedded in
  it leaks. It is also unnecessary: the server re-reads authoritative state at
  redemption regardless, so a self-contained claim would be re-verified and
  therefore pointless.
* **Server-side lookup, not signature verification.** The backend resolves the
  token to its gift card, that card's current exact owner, and its
  Ledger-derived balance. This is what makes the credential "followable" back to
  the cardholder while keeping the code itself meaningless.
* **PostgreSQL, not Redis.** Token state lives beside the card, the provision,
  and the Ledger, so single-use consumption is enforced by the same
  transactional guarantees that already protect money, in the same
  `SERIALIZABLE` boundary. Redis has never been introduced (ADR-002 forbids it
  becoming authoritative anyway), and a ~60-second lookup does not justify a new
  piece of infrastructure to run, secure, and monitor.
* **TTL of 60 seconds**, matching the refresh cadence in PROJECT_DEFINITION §19.
  Persisted rows carry a server-generated `expires_at_utc`.
* **Single-use.** A token is consumed exactly once, under a row lock, in the
  same transaction that creates the payment provision (ADR-033). A consumed,
  expired, or unknown token is refused identically, so the response is not an
  oracle for which case applied.
* **No clock-skew allowance is required.** Validity is evaluated only against
  the server clock; neither the phone nor the till asserts a time. Skew
  tolerance would only be needed for a self-contained token, which is precisely
  what this decision rejects.
* **Independent of POS authentication.** The token identifies the *spending
  context*; POS client and terminal credentials identify *who is asking*
  (DOMAIN_RULES §10.10). Both are required. Possessing a token is not authority
  to charge, and being an authenticated till is not authority to charge a
  specific card.

### Consequences

* Phase 4 adds a token table with forced RLS, a server-generated expiry, and
  single-use consumption; no Redis dependency enters the stack.
* A leaked or photographed code is useful only within its TTL, only once, and
  only to an authenticated POS client.
* Token rotation is a client concern: the cardholder app requests a fresh token
  as the previous one nears expiry.
* Because the token carries nothing, no contract change is needed if the card,
  owner, or balance model evolves.

---

## ADR-018 — Redemption Idempotency-Key Derivation

**Status:** Accepted
**Date:** 2026-08-03
**Blocks:** Phase 4 redemption

### Context

Retries at a till are normal, not exceptional: the network drops after the
backend committed but before the till saw the response, the cashier taps twice,
the terminal reboots mid-sale. Something must recognise a repeat of the *same*
purchase and return the original outcome instead of charging again.

The open question was who owns that identity. If the till chooses it freely, the
guarantee depends on every till behaving correctly — a reused value charges two
different sales as one, and a regenerated value charges one sale twice.

### Decision

**The server-issued QR token is the identity of the purchase.**

* The redemption idempotency key is derived from the token's server-side
  identity, not from any client-chosen value. The backend issued that token,
  knows it is single-use (ADR-017), and can refuse a second attempt regardless
  of what the till sends.
* The POS transaction reference, store identity, and terminal identity are
  **recorded with the redemption** for reconciliation, receipts, and dispute
  handling. They are not what provides the guarantee.
* Uniqueness reuses the existing Ledger mechanism from ADR-014:
  `operation_type + idempotency_key` is unique, so a duplicate posting is
  impossible at the database level rather than only in application logic.
* A retry presenting the same token returns the original redemption outcome. A
  token already consumed by a *different* completed redemption is a conflict,
  not a silent duplicate — it is refused rather than quietly treated as a
  repeat.
* `SERIALIZABLE` isolation and the existing card-scoped advisory lock keep
  redemption concurrency-safe against sharing reservations, payment provisions,
  and lifecycle changes (ADR-026, ADR-033).

### Consequences

* Double-charging is prevented on the side the platform controls. A buggy or
  hostile till cannot cause one, and cannot block a legitimate sale by
  mismanaging its own reference.
* POS vendors have less to implement correctly: they present the token and their
  own reference, and need no idempotency protocol of their own.
* The POS transaction reference remains available for reconciliation without
  ever being load-bearing for financial safety.

---

## ADR-043 — POS Client Boundary

**Status:** Accepted
**Date:** 2026-08-03
**Blocks:** Phase 4 POS repository

### Context

POS/counter software will live in its own repository. The question raised was
whether it reaches PostgreSQL directly or goes through the platform API.

### Decision

**The POS authenticates as a POS client and calls `/api/v1`. It is never given
database credentials.**

Every safety property this platform relies on lives in application services and
policies above the tables: named-permission evaluation, Row-Level Security
session context, Ledger balancing and immutability, append-only audit,
idempotency, and overspend protection. A till holding a database login would sit
underneath all of it, and a defect in POS SQL could create or destroy value with
nothing in the way. Distributing database credentials to physical devices in
stores also makes rotation and revocation impractical.

This is the same boundary ADR-037 already sets for the portal and cardholder
clients; POS is a third client class, differing only in that it authenticates as
a device rather than as a person (DOMAIN_RULES §10.10).

### Consequences

* Phase 4 adds POS client and terminal authentication plus redemption endpoints;
  it adds no direct database access path.
* The POS repository can be developed and released independently against the
  versioned OpenAPI contract, as the portal and cardholder repositories are.
* PostgreSQL remains authoritative for the final redemption outcome
  (DOMAIN_RULES §10.15) without the POS ever addressing it directly.

---

## ADR-019 — PostgreSQL Database Roles

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Sprint 1 persistence

### Context

RLS-based tenant isolation (ADR-005) and append-only audit (ADR-008) both depend
on a disciplined database-role model. RLS does not apply to superusers or roles
with `BYPASSRLS`, and append-only guarantees require withholding UPDATE/DELETE
from the runtime role.

### Decision

Use at least two distinct PostgreSQL roles.

**Migration owner**

* Owns schemas and migrations.
* Creates tables, policies, constraints, and database grants.
* Is not used by the running application.

**Runtime application role**

* Non-superuser.
* No `BYPASSRLS`.
* Subject to RLS.
* No schema ownership.
* Holds only the required SELECT, INSERT, UPDATE, and DELETE privileges.
* Has no UPDATE or DELETE privilege on committed audit records.
* Has no UPDATE or DELETE privilege on committed ledger entries when the Ledger
  module is introduced.

Additional read-only or reporting roles may be added later when required.

### Grant Mechanism (added during IMPL-001)

Module schemas are created during database provisioning rather than only by EF
migrations, so that `ALTER DEFAULT PRIVILEGES FOR ROLE <migrator> IN SCHEMA
<schema>` can be attached before any table exists. Every table the migration
owner subsequently creates then inherits the intended grants automatically:

* `organizations` — SELECT, INSERT, UPDATE, DELETE to the runtime role
* `audit` — SELECT, INSERT only

This avoids having to re-grant after each migration and is what keeps the audit
schema append-only for the runtime role without relying on application code.

### Consequences

* Migrations and application runtime use different roles.
* Append-only audit and (later) append-only ledger are enforced at the privilege
  level, not only in application code.
* Tests must run application behavior through the runtime role, not the
  migration owner (see ADR-022).
* Platform-level cross-tenant access must use a controlled execution context and
  RLS policy path (ADR-020), never a superuser connection.

---

## ADR-020 — Execution-Context and RLS Propagation

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Tenant enforcement

### Context

Tenant isolation must be enforced for both reads and writes, must work outside
HTTP (background jobs, tests), and must be safe under connection pooling.

### Decision

Use a scoped `IExecutionContext` abstraction carrying trusted server-side values,
including at least:

* `UserId`
* `ActiveMembershipId`
* `ActiveOrganizationId`
* `IsPlatformOperator`
* `CorrelationId`

Rules:

* Domain and application layers must not depend on `HttpContext`.
* HTTP middleware may populate the scoped execution context after validating the
  authenticated user and active membership.
* Background jobs and tests must create and populate the same abstraction
  explicitly.
* Do not use `AsyncLocal` unless a concrete future requirement demonstrates a
  scoped service is insufficient.

**RLS session context**

* A `SaveChangesInterceptor` may stamp `organization_id` on new tenant-owned
  entities, reject mismatched tenant ownership, and add audit metadata — but it
  must **not** be the only mechanism that sets RLS session context, because RLS
  must also protect read queries.
* RLS session variables must be established before any tenant-scoped SQL command
  executes.
* Use a transaction-scoped `SET LOCAL` (or an equivalent safe Npgsql strategy)
  that works for reads and writes, is safe with connection pooling, cannot leak
  one tenant's context into another request, and can be explicitly configured in
  tests and background jobs.

### Consequences

* The execution-context shape propagates through every handler; defining it as
  non-HTTP-coupled now avoids a later sweep of all call sites.
* A transaction-scoped RLS-context mechanism is required at scaffolding.
* Integration tests must prove that pooled connections cannot reuse stale tenant
  context (see ADR-022).

---

## ADR-021 — Platform Scope Versus Customer Organization Hierarchy

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Organization schema

### Context

The platform owner sits above customer organizations, but modeling it as an
ordinary organization row would complicate `organization_id` nullability, RLS,
and hierarchy-depth counting.

### Decision

* The platform operator is a distinct **platform scope**, not a normal row in the customer
  organization hierarchy.
* Customer organizations and their subsidiaries form their own hierarchy; the
  platform scope is not counted in the ADR-010 depth limit.
* Platform operators exist outside the customer hierarchy. Platform staff use global
  user identities with **separate platform-role assignments** (ADR-006).
* Customer users use organization memberships and organization-role assignments.
* Platform operations must still be permission-based and audited.
* Being a platform operator must not automatically imply every platform
  permission.

### Consequences

* Tenant-owned tables reference customer organizations only; platform-scoped and
  global entities are classified separately (ADR-005).
* Platform authorization is a distinct model from organization-membership
  authorization.
* RLS policies must account for a controlled platform-operator path without
  disabling RLS.

---

## ADR-022 — Initial Test Strategy

**Status:** Accepted
**Date:** 2026-07-22
**Blocks:** Test scaffolding

### Context

The project prioritizes integration tests for tenant isolation, authorization
boundaries, database constraints, membership lifecycle, and audit generation.
RLS and PostgreSQL constraints cannot be validated by the EF Core InMemory or
SQLite providers.

### Decision

Create three test projects when scaffolding begins:

* `UnitTests`
* `IntegrationTests`
* `ArchitectureTests`

The integration-test harness must use:

* Testcontainers
* `WebApplicationFactory`
* Real PostgreSQL
* The actual RLS policies
* The runtime application database role (ADR-019), not the migration owner
* Explicit platform and tenant execution contexts (ADR-020)

Architecture tests must verify:

* Modules do not reference another module's implementation assembly.
* Modules reference only permitted `.Contracts` assemblies.
* Domain code does not depend on EF Core.
* Domain code does not depend on ASP.NET Core.
* Domain code does not depend on Redis or Elasticsearch.
* No module accesses another module's DbContext.

Do not create one test project per module until test volume justifies it.

### Consequences

* Integration tests exercise real isolation, constraints, and RLS behavior.
* The dependency rules of ADR-004 and ADR-011 are expressed as architecture tests.
* The EF Core InMemory and SQLite providers are excluded as substitutes for
  PostgreSQL-specific behavior.

### Status note (REVIEW-001, 2026-07-23)

A CI pipeline now exists at `.github/workflows/ci.yml`: it builds in Release and
runs the architecture, unit, and integration suites on every push and pull
request. The integration job uses Testcontainers against real PostgreSQL on the
GitHub-hosted runner, so the RLS policies and database privileges are exercised
by the gate rather than by hand. Test results are uploaded as artifacts.

The workflow has not yet been observed running on GitHub — it is committed but
unproven until the branch is pushed.

---

## ADR-023 — Tenant-Boundary RLS on the Organizations Table

**Status:** Accepted
**Raised:** 2026-07-23 (during IMPL-003)
**Accepted:** 2026-07-24
**Blocks:** Organizations tenant isolation

### Context

`PROJECT_DEFINITION.md` §25 and ADR-005 classify `Organizations` as
**tenant-scoped**, so it should sit behind a Row-Level Security policy. It does
not today: IMPL-001 created it as a platform-managed table with no policy, and
IMPL-002 added RLS only to `organization_memberships`.

IMPL-003 introduced the first **customer** write to the organizations table
(subsidiary creation). Scope is currently enforced in the application service
from the trusted execution context — the parent must equal the caller's
`ActiveOrganizationId` — with integration tests proving cross-tenant creation is
rejected. Per DOMAIN_RULES §5.11 that is not sufficient on its own: RLS is meant
to be the authoritative barrier.

The correct policy predicate is hierarchy-aware — a caller may see its own
organization and its descendants, i.e. `hierarchy_path <@ <caller subtree>` —
which is the **same predicate** ADR-006 `Subtree` scope requires. Building a
narrower interim policy (for example, own row plus direct children) would have to
be rewritten one slice later.

### Open question

How is the caller's subtree propagated to the database so the policy can be
evaluated without recursion?

Candidate approaches:

* **Additional session variable** — `SessionContextWriter` also sets
  `app.organization_path`. Requires the caller's ltree path to be known when the
  context is established, which currently means an extra lookup per request.
* **`SECURITY DEFINER` lookup function** — the policy calls a `STABLE` function
  that resolves `app.organization_id` to its path. Avoids the extra round trip
  but interacts with `FORCE ROW LEVEL SECURITY` (the definer is also subject to
  the policy) and requires careful `search_path` pinning.
* **Denormalized root/tenant column** on the organizations table, if a coarser
  boundary proves sufficient.

The decision must also state whether platform operators keep a read-only path (as
for memberships), and what happens to the existing platform-scoped reads and the
integration tests that currently query the table with no session context.

### Decision

**The RLS predicate is the tenant boundary, not the authorization scope**, and the
caller's tenant is resolved by a `SECURITY DEFINER` function.

* The policy admits rows whose `root_organization_id` matches the caller's tenant
  root. A caller therefore sees its whole customer hierarchy — root and all
  descendants — and nothing of any other customer.
* **Which part of its own tenant a caller may act on is authorization's job**
  (ADR-006 scope evaluation), enforced above the database. Splitting the concerns
  this way means the policy is written once: adding `Subtree` or
  `SelectedOrganizations` scope later changes application authorization, not this
  predicate. This supersedes the earlier assumption that the policy itself had to
  be hierarchy-aware via `hierarchy_path <@ …`.
* `organizations.caller_root_organization_id()` resolves the tenant root from
  `app.organization_id`. It is `STABLE` (evaluated once per query), `SECURITY
  DEFINER`, and has a pinned `search_path`. It takes no arguments and exposes
  nothing beyond the caller's own session context, so the default `PUBLIC`
  execute grant is kept — which also keeps the migration independent of the
  runtime role's name, which differs per environment.
* The table uses `ENABLE ROW LEVEL SECURITY`, **deliberately not `FORCE`**.
  `FORCE` would subject the table owner to the policy, which would break the
  definer lookup the policy depends on. The owner is the migration role, which by
  ADR-019 is never used at runtime; the runtime application role owns nothing and
  remains fully subject. This is the one accepted difference from
  `organization_memberships`, which keeps `FORCE`.
* **Platform writes are restricted to root organizations.** `WITH CHECK` admits a
  platform operator only when `parent_organization_id IS NULL`, so a platform
  operator can create customer organizations but cannot inject a subsidiary into
  a customer's tree. Every other write must land inside the caller's own tenant.
  Platform reads remain unrestricted, which is what the customer-management and
  support flows need.

### Rejected alternative

Carrying the caller's `ltree` path in a session variable was rejected: resolving
it in the application requires reading the organizations table *before* the
session context exists, which the policy itself would block. Solving that needs a
bootstrap disjunct plus a second round trip per request, for no security gain.

### Consequences

* Both tenant-owned tables are now behind RLS; application filtering is no longer
  the only barrier anywhere.
* A raw connection with no session context sees **nothing** — RLS fails closed.
  Tests that verify database state must say which tenant they act as; the
  `ScopedSqlSession` helper exists for this.
* The migration role can still bypass the policy on this table. It must remain
  out of runtime use (ADR-019).
* Eight integration tests prove the boundary, including cross-tenant read, insert,
  update and delete denial with no application filter present, the platform read
  path, the platform subsidiary-injection denial, and fail-closed behaviour with
  no context.

---

## ADR-024 — Organization Code Uniqueness Scope

**Status:** Accepted
**Raised:** 2026-07-23 (REVIEW-001, finding B1)
**Accepted:** 2026-07-23
**Blocks:** Subsidiary creation — this was a live security issue, now resolved

### Context

`ux_organizations_code` makes `code` unique across **all** organizations. That was
defensible while only the platform operator created organizations. IMPL-003 lets customers choose
codes for their own subsidiaries, which makes a single global namespace harmful:

* **Disclosure.** A `409 Conflict` tells customer B that some other tenant already
  holds a code, turning the endpoint into an existence oracle over the whole
  customer base. Violates DOMAIN_RULES §4.10 and §5.9.
* **Collisions and squatting.** At thousands of organizations, two customers
  wanting `RETAIL` or `HQ` is the expected case, and whoever asks first denies it
  to everyone else permanently.

The duplicate pre-check in `SubsidiaryService` compounds this: it queries the
organizations table with no tenant predicate, and that table has no RLS.

### Options

* **Per-tenant subsidiary codes, global root codes** — root/customer codes stay
  globally unique (the platform operator assigns them, they are platform-wide references);
  subsidiary codes are unique within the owning customer, via a
  `root_organization_id` column plus PostgreSQL partial unique indexes.
* **Per-parent codes** — unique within the immediate parent only. Simpler, but
  permits the same code twice inside one customer at different branches, which
  will confuse human-facing references.
* **Keep global, platform-assigned only** — customers no longer choose codes.
  Removes self-service, contradicts the IMPL-003 flow.
* **Keep global, obscure the error** — returns a non-committal error on conflict.
  Hides the oracle but leaves collisions and squatting untouched. Rejected as
  treating the symptom.

### Decision

**Per-tenant subsidiary codes, global root codes.** Implemented 2026-07-23.

* `Organization` carries `RootOrganizationId` — the owning customer. A root
  organization is its own root; a subsidiary inherits its parent's.
* `ux_organizations_code` is replaced by two partial unique indexes:
  * `ux_organizations_root_code` — unique on `code` where
    `parent_organization_id IS NULL` (the platform-wide root namespace).
  * `ux_organizations_tenant_code` — unique on `(root_organization_id, code)`
    where `parent_organization_id IS NOT NULL` (per-customer subsidiary
    namespace).
* Both duplicate pre-checks are scoped to match: the root check is restricted to
  roots, and the subsidiary check to the caller's own tenant.
* The migration backfills `root_organization_id` from the `ltree` path before
  building the unique indexes, then drops the transient column default.

### Consequences

* Two customers may each name a subsidiary `RETAIL`; neither can discover the
  other's codes by provoking a conflict.
* Root codes remain a single platform namespace, so platform-assigned customer codes
  stay globally unambiguous.
* A code is only unique within its tenant, so any future lookup by code alone
  must also carry the tenant — code is no longer a global key for subsidiaries.
* Integration tests cover both directions: two tenants sharing a subsidiary code
  succeeds, and duplicate root codes are still rejected.

---

## ADR-025 — Auditing Denied and Failed Operations

**Status:** Accepted
**Raised:** 2026-07-23 (REVIEW-001, finding H2)
**Blocks:** Security monitoring; production readiness

### Context

Audit records are written only on success. Every `Require…` check throws
`ForbiddenException` before a transaction is opened, so denied attempts — including
cross-tenant probing — leave no trace at all.

This follows from ADR-011: audit commits atomically with the business change, so a
failure audit written inside the failing transaction would be rolled back with it.
Failure auditing therefore needs a path that deliberately survives the rollback.

`DOMAIN_RULES.md` §6.7 anticipates this ("Failed sensitive operations may require
audit records") but nothing implements it, and `AuditOutcome.Failure` is currently
never used.

### Options

* A separate connection/transaction for failure audit, committed independently.
* Post-rollback write in the exception handler, from the trusted execution context.
* Emit denials to the logging/SIEM pipeline rather than the audit table, treating
  the audit table as a record of *effected* changes only.

### Decision

**A separate connection, written from a single central hook, for authenticated
callers only.** Implemented 2026-07-24.

* `IAuditRecorder.RecordIndependentlyAsync` writes on a connection obtained from
  `IDatabaseConnectionFactory` and commits immediately. It cannot use the scoped
  connection, which is inside the very transaction being rolled back.
* The hook lives in `AppExceptionHandler`, which sees every
  `ForbiddenException` regardless of which module raised it. A single chokepoint
  cannot be forgotten when a new service is added, whereas per-service calls
  reliably rot. Entry points outside the HTTP pipeline — background jobs, when
  they exist — must record their own.
* **Only authenticated callers are recorded.** An unauthenticated request has no
  principal worth attributing, and recording them would let anyone fill the audit
  table by hammering a protected route. This is the answer to the flooding
  concern; rate limiting and sampling were not needed.
* A failure to write the denial record never changes the response: it is logged
  and swallowed, so auditing cannot turn a clean 403 into a 500.
* Denials are also logged at Warning with the correlation id, so the same event
  is visible to log-based monitoring without querying the audit table.

### Consequences

* `AuditOutcome.Failure` is now used, and `authorization.denied` records carry
  the actor, actor type, organization scope, HTTP method, path, and reason code.
* Ordinary audit records remain atomic with the change they describe; the
  independent path exists only where surviving a rollback is the point.
* Five integration tests cover the organization-scoped denial, the platform
  denial, the recorded fields, the unauthenticated exclusion, and that the record
  survives while the refused operation writes nothing.

---

## ADR-026 — Transaction Nesting and Isolation for Financial Operations

**Status:** Accepted
**Raised:** 2026-07-23 (REVIEW-001, finding H3)
**Blocks:** Sprint 2 — Ledger and all value-changing operations

### Context

`TransactionCoordinator` today throws if a transaction is already in progress, and
always uses PostgreSQL's default `READ COMMITTED`.

1. **Nesting.** Phase 2 operations span modules — a distribution touches Ledger,
   Gift Cards, and Distribution. As soon as one service calls another that calls
   `BeginAsync`, it throws. The coordinator needs join-or-begin semantics with a
   single commit at the outermost scope.
2. **Isolation.** `PROJECT_DEFINITION.md` §15 requires that concurrent operations
   cannot overspend. Under `READ COMMITTED` a read-balance-then-debit sequence
   permits lost updates. Preventing overspend requires `SERIALIZABLE`, explicit
   `SELECT … FOR UPDATE` locking, or a database-enforced balance invariant — and
   the coordinator exposes no way to request any of them.

Fixing this while there are three call sites is far cheaper than after the
financial modules depend on the current shape.

### Decision

**Join-or-begin nesting, with isolation requested at the outermost scope.**
Implemented 2026-07-24.

* `BeginAsync` joins an in-progress transaction instead of throwing. The
  outermost scope owns the database transaction and is the only one that commits;
  a nested scope can enlist contexts and signal success.
* **A nested scope abandoned without completing dooms the whole transaction.**
  The outermost `CommitAsync` then refuses, so a failed inner operation can never
  be partially persisted by an outer one that carries on regardless.
* `BeginAsync(IsolationLevel, …)` requests a level. Joining an in-progress
  transaction that is *weaker* than requested throws, because PostgreSQL cannot
  raise isolation after a transaction has started and silently returning a weaker
  guarantee than the caller asked for is how overspend bugs get written. Joining
  a stronger one is allowed.
* The default remains `ReadCommitted`, which suits the administrative operations
  built so far.

**Overspend protection:** value-changing operations will begin at
`Serializable`. An integration test demonstrates the mechanism — two concurrent
serializable transactions reading then writing the same row cannot both commit,
failing with a `40xxx` serialization error.

### Financial retry policy (resolved with ADR-014)

Initial financial operations use `Serializable` and do not retry invisibly.
PostgreSQL `40001` failures are returned as retryable conflicts, and the caller
repeats the request with the same idempotency key. A bounded server-side retry
policy may be added later only when operational evidence justifies it.

### Consequences

* One business operation can span modules that each own a transaction boundary,
  which is what Sprint 2 requires.
* Callers must not assume `BeginAsync` gives them a fresh transaction; only
  `IsOutermost` says so.
* Nine integration tests cover joining, nested commit, the abandoned-nested-scope
  rule, release on disposal, isolation propagation, both isolation-mismatch
  directions, and the serialization-failure behaviour.

---

## ADR-027 — Public API Versioning Strategy

**Status:** Accepted
**Raised:** 2026-07-23 (REVIEW-001, finding H4). Previously tracked informally in
`PROJECT_DEFINITION.md` §29.
**Accepted:** 2026-07-24
**Blocks:** The first external client

### Context

All routes are currently unversioned (`/api/organizations…`). This is the open
decision whose cost rises fastest with time: today it is a string change across
five endpoint registrations; after a POS integration or mobile app ships it
becomes a coordinated multi-party migration. POS terminals are long-lived and
update slowly, so the platform will need to serve old contracts for years.

### Options

* URL segment (`/api/v1/…`) — simplest, most visible, cache- and log-friendly.
* Header or media-type versioning — cleaner URLs, harder for POS vendors.
* No versioning, additive-only changes — fragile once contracts are external.

### Decision

**URL segment versioning, adopted immediately.** All public routes are served
under `/api/v1/…`, defined by the `ApiRoutes.V1` constant so the prefix has one
source of truth.

A breaking change means introducing the next prefix and serving both for as long
as clients need, never altering an existing version in place. Additive,
non-breaking changes stay within the current version.

### Consequences

* Applied while there were no external consumers, so the change cost nothing
  beyond a mechanical rename of five endpoint groups, the demo page, and the
  integration-test URLs.
* Long-lived POS terminals can pin a version and keep working across releases.
* The version is visible in logs, caches, and access records, which makes
  client-version debugging tractable.

---

## ADR-028 — Identity Password and Token-Session Baseline

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** Identity implementation

### Context

ADR-007 establishes global users and ADR-009 requires rotating refresh-token
families with reuse detection, but the concrete login identifier, password
policy, token lifetimes, and token transport were not fixed.

### Decision

* Staff and administrator login identifiers are globally unique normalized
  email addresses. ADR-034 extends recipient identities to exactly one email or
  E.164 phone login contact.
* Passwords are 12–128 Unicode characters. Spaces and all character classes are
  allowed; no uppercase, digit, or symbol composition rule is imposed.
* Common passwords are rejected by a local blocklist. Passwords are never
  silently truncated.
* Passwords use the established ASP.NET password hasher. Custom password
  cryptography is forbidden.
* Access tokens are signed JWTs valid for 15 minutes.
* Refresh tokens are opaque random values valid for 30 days and returned through
  explicit JSON API contracts.
* Only a cryptographic hash of each refresh token is stored.
* Every successful refresh consumes the presented token and creates exactly one
  replacement in the same family.
* Presenting a consumed or revoked token is reuse detection and revokes the
  associated session.
* Login is rate-limited and credential failures do not disclose whether the email
  exists.
* JWT signing material is required configuration and is never committed.

### Consequences

* Password managers and long passphrases are supported.
* Clients are responsible for secure refresh-token storage; browsers should use
  an appropriate secure client-side/backend-for-frontend design rather than
  ordinary JavaScript storage.
* Session state remains in PostgreSQL, enabling explicit refresh revocation and
  compromise detection.
* Access tokens remain valid for at most 15 minutes after revocation unless a
  later requirement introduces per-request session introspection.
* Password reset, email verification, MFA, and signing-key rotation are separate
  hardening tasks.

---

## ADR-029 — One-Time Platform-Administrator Bootstrap

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** Phase 1 bootstrap

### Context

Platform operations require database-backed platform-role assignments, but the
first operator cannot be created through an endpoint that already requires one.
Manual SQL would bypass application validation and audit, while an indefinitely
available bootstrap credential would leave a permanent privileged entry point.

### Decision

Expose a rate-limited one-time bootstrap endpoint. It requires a high-entropy
secret from configuration, supplied in a dedicated request header and compared
in constant time. The secret is never stored in PostgreSQL, logs, or audit.

A singleton bootstrap-state row is locked in the same transaction that creates
the first user, built-in Platform Administrator role, complete platform
permission grant, role assignment, and audit record. Completing that transaction
permanently disables further bootstrap attempts. Concurrent requests serialize
on the state row, so at most one can succeed.

The built-in role and its grants are persisted in the Authorization module.
JWT authentication derives platform authority only from effective persisted
platform-role assignments; a JWT subject alone is never a platform operator.

### Consequences

* Initial deployment requires `Bootstrap:PlatformAdministrator:Secret` with at
  least 32 UTF-8 bytes; it should be removed from runtime configuration after
  successful bootstrap.
* Bootstrap is deployable without manual database mutation and leaves an audit
  trail.
* Losing the secret before bootstrap requires an operator-controlled
  configuration change; after bootstrap it has no authority.
* General platform-role management remains a separate future capability.

---

## ADR-030 — Phase 2 Gift-Card Funding, Delivery, and Lifecycle Baseline

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** IMPL-012 through IMPL-015

### Context

The original Phase 2 plan assumed that distribution targeted an existing active
user in the purchasing organization's subtree. The product vision instead
requires a company to send a card to an email address or phone number even when
the recipient has no account yet. Funding ownership, issuing scope, card
defaults, value return, and bulk semantics also need to be stable before the
Gift Cards schema is introduced.

### Decision

* Issuance moves value from the root customer's corporate-credit balance into a
  card owned by organization inventory.
* `funding_organization_id` is the root customer that economically owns the
  corporate credit. `issuing_organization_id` is the organization or department
  whose authorized member performed the issuance. They are recorded separately.
* Distribution changes ownership and delivery state only. It does not create a
  ledger posting when no value moves.
* `valid_from` defaults to the server issuance time and `expires_at` is required.
* Transferability and divisibility are explicit per-card policies and default to
  `false`.
* A recipient need not have a pre-existing account or organization membership.
  Distribution creates an immutable invitation addressed to a normalized email
  address or phone number. Its high-entropy, expiring, single-use claim token is
  stored only as a hash.
* Claim either associates a matching verified identity or creates the minimum
  identity needed to activate and own the card. Receiving a card never grants an
  organization membership or administrative permission automatically.
* Cancellation and expiration return remaining value to the card's funding
  organization through compensating ledger entries.
* The initial bulk operation is synchronous, all-or-nothing, idempotent, and
  limited to 100 items.

### Consequences

* IMPL-012 must model funding and issuing organizations separately and support
  identity-owned cards without requiring an organization membership.
* IMPL-013 replaces the earlier "eligible active user only" wording with a
  secure invitation and claim flow. Direct assignment to an existing verified
  user may remain an optimization, not a prerequisite.
* Email/SMS provider integration stays behind a notification contract; a
  development sink is sufficient in Phase 2.
* Card receipt does not expand customer-organization authorization.

---

## ADR-031 — Tenant Root Versus Active Operational Organization

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** Phase 2 prerequisite

### Context

Authorization already supports `Organization`, `Subtree`, and
`SelectedOrganizations` scopes, but several query filters and RLS policies used
exact equality with the active organization. A valid root membership with
subtree authority could therefore pass permission evaluation and still be
unable to read or write a descendant row.

Financial operations also need both the organization where an action occurs and
the root customer that owns the corporate value.

### Decision

* `active_organization_id` is the organization that owns the verified active
  membership and anchors permission evaluation.
* `tenant_root_organization_id` is resolved from PostgreSQL during membership
  authentication and is the customer data-isolation and funding boundary.
* RLS admits rows only when their owning organization belongs to the caller's
  tenant root. It does not itself grant permission to act on those rows.
* EF Core filters use the same PostgreSQL tenant-root predicate for
  organization-owned records. Root-keyed financial filters compare against the
  verified tenant root.
* Application services continue to require named permissions against the exact
  target organization. Hierarchy alone grants no authority.

### Consequences

* A root administrator with explicit subtree scope can manage an authorized
  descendant without weakening cross-tenant isolation.
* A descendant or unrelated tenant is still denied unless its target is covered
  by the caller's assignments.
* Future card rows must distinguish their tenant/funding root from their
  operational issuing organization instead of overloading one identifier.

---

## ADR-032 — Tenant-Isolated Audit and Membership Attribution

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** Phase 2 prerequisite

### Context

Audit storage was append-only but readable through a context-free runtime
connection, and organization-member records did not persist the active
membership. That was insufficient before customer financial and card history
became queryable.

### Decision

* Organization-member audit records require `actor_membership_id` in addition to
  the global user and organization scope.
* The audit table is protected by enabled and forced PostgreSQL RLS.
* A customer context may read audit rows scoped anywhere in its tenant root.
  Application audit APIs, when added, still require an explicit named
  permission.
* A platform context may read across tenants through its controlled RLS path.
* An identity-only context may read only its own global audit rows.
* Organization-scoped inserts must belong to the caller's tenant unless the
  caller is an authorized platform operation. Append-only database privileges
  remain unchanged.

### Consequences

* A context-free runtime connection sees no audit history.
* Financial and lifecycle actions can be attributed to the exact membership
  whose permission authorized them.
* A tenant-safe audit query API remains a later read surface; this decision
  establishes its storage boundary first.

---

## ADR-033 — First-Class Payment Provision Lifecycle

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** Phase 4 payments

### Context

ADR-014 intentionally deferred pending reservations, while the product vision
requires a POS payment to create a time-bounded provision before final
confirmation. Modeling only final redemption would leave no authoritative way
to prevent a cardholder from sharing or spending value already promised to a
terminal.

### Decision

Phase 4 introduces a first-class provision with `Active`, `Confirmed`,
`Cancelled`, and `Expired` states.

* Creating a provision atomically validates the card and reserves an amount for
  a bounded TTL.
* Active provisions reduce available value but are not posted ledger value
  movements.
* Confirming a provision consumes it exactly once and posts the redemption
  ledger transaction atomically.
* Cancelling or expiring a provision releases its reservation without a ledger
  posting.
* Create, status, confirm, cancel/release, and expiration operations are
  idempotent and safe against concurrent sharing, other provisions, and direct
  redemption.
* PostgreSQL remains authoritative. Redis may accelerate ephemeral lookups only
  after a measured need and never becomes the final provision or payment record.

### Consequences

* Phase 4 must expose available versus reserved value and explicit provision
  status.
* Card-level concurrency must cover sharing, provision creation, confirmation,
  cancellation, and redemption as one protocol.
* Final payments, reversals, and refunds remain immutable posted ledger
  operations; provisions do not weaken ADR-014's posted-ledger rules.

---

## ADR-034 — Staff and Recipient Login/Delivery Identifiers

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** IMPL-013
**Supersedes:** ADR-028's email-only login-identifier statement for recipient
identities only

### Context

platform operators, company administrators, and company staff are provisioned
through controlled administrative workflows where email is the appropriate
business identifier. A gift-card recipient is different: the company must be
able to send a card to either an email address or a smartphone number without
first creating staff access.

Sending email or SMS on every visit would add cost and make authentication
dependent on a delivery provider even after activation. The platform already
has password authentication, 15-minute JWT access tokens, and rotating 30-day
refresh sessions.

### Decision

* platform users and organization staff created through bootstrap or user
  administration remain email-only.
* A recipient identity has exactly one normalized login contact: either a
  globally unique email address or a globally unique E.164 phone number.
* Distribution selects one channel and sends one activation message through
  that channel. IMPL-013 provides a notification contract and Development
  delivery sink; real email and SMS providers remain separate integrations.
* Possession of the single-use claim token verifies control of the selected
  contact for this activation. The raw token is never persisted, logged, or
  audited.
* Claim reuses an existing active identity with the same normalized contact
  without changing its password. A new recipient supplies a password governed
  by ADR-028.
* Later sign-ins use the recipient's email or phone plus password. Successful
  login and refresh use the existing JWT/refresh-token protocol and send no
  email or SMS.
* Receiving or claiming a card creates no organization membership, role, or
  administrative permission.

### Consequences

* Routine recipient access has no email/SMS delivery cost and remains available
  when a notification provider is temporarily unavailable.
* The Identity schema enforces exactly one contact for every account, while
  existing staff APIs continue to accept email only.
* Phone normalization and uniqueness become security invariants rather than UI
  conventions.
* Passwordless OTP login, activation resend, contact change, MFA, and real
  provider retry/outbox behavior require explicit future use cases; they are
  not silently introduced by IMPL-013.

---

## ADR-035 — Gift-Card Lifecycle Authority and Terminal-State Policy

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** IMPL-014

### Context

ADR-030 requires cancellation and expiration to return remaining card value to
the funding organization, but it does not decide who may change lifecycle
state, whether a company may reclaim a card after activation, how suspension
interacts with an unclaimed invitation, or how time-based expiration is made
financially final.

Those choices affect ownership guarantees, permissions, API contracts,
distribution-token behavior, ledger compensation, audit attribution, and the
valid transition matrix. They must be fixed before IMPL-014 implements any
dependent behavior.

### Decision

* A platform administrator with an explicit platform permission may
  suspend or reactivate any non-terminal card and may emergency-cancel any
  non-terminal card. Emergency cancellation requires a normalized reason.
* A company administrator with an explicit organization permission may manage
  eligible cards in the permission-checked organizational scope of its funding
  tenant.
* A company may cancel an organization-inventory or awaiting-claim card. Once a
  recipient claims a card and becomes its owner, the company may no longer
  cancel it. Only an authorized platform administrator may
  emergency-cancel a claimed card.
* An authenticated cardholder may suspend and reactivate only a card they own.
  Cardholders cannot cancel cards or return value to a company.
* Suspension never changes ownership or moves ledger value. Suspending an
  awaiting-claim card pauses activation. Reactivation restores
  `AwaitingClaim` when ownership is awaiting claim and restores `Active` for
  organization-inventory or identity-owned cards.
* `Cancelled` and `Expired` are terminal states. Cancellation and expiration
  return the exact remaining ledger-derived value to the funding organization
  once and only once, while retaining ownership and distribution provenance as
  history.
* Expiration is effective immediately at the server-evaluated
  `expires_at_utc`. A PostgreSQL-coordinated worker finalizes the idempotent
  value return; a permission-checked per-card operation remains available for
  deterministic administration and recovery.
* Administrative lifecycle operations require a normalized reason and an
  idempotency key. All lifecycle operations append immutable lifecycle and
  audit history; financial terminal operations also reference their immutable
  compensating ledger transaction.

### Consequences

* Recipient ownership after claim cannot be silently revoked by the issuing
  company, while the platform operator retains a controlled fraud/compliance intervention path.
* Organization, platform, and owner endpoints require separate named
  permissions or ownership checks; role names alone grant no authority.
* Ownership state and lifecycle state remain separate. Reactivation is derived
  from ownership instead of storing or trusting a caller-selected prior state.
* A suspended pending invitation remains historically present but cannot be
  claimed; cancellation or expiration closes it terminally.
* The expiration coordinator must use PostgreSQL locking/idempotency and may not
  introduce Redis, a mutable balance field, or a second financial model.
* Concurrency tests must cover activation against suspension, cancellation, and
  expiration, plus duplicate terminal processing.

---

## ADR-036 — Phase 2 Read-Only Reporting and Reconciliation Boundary

**Status:** Accepted
**Date:** 2026-07-27
**Blocks:** IMPL-016 and Phase 2 exit

### Context

Phase 2 has authoritative data spread across Corporate Credits, Ledger, Gift
Cards, Distribution, and Audit. Finance teams need one coherent history and
summary; recipients need their own balances/history; support needs
tenant-isolated investigation; and operations need a way to detect divergence.
Adding a copied projection or an automatic repair path at this scale would
create avoidable synchronization and authority risks.

### Decision

* The Reporting supporting module owns read contracts and query composition but
  no schema, mutable balance, copied financial history, or repair command.
* Phase 2 reports use parameterized PostgreSQL reads inside the normal
  transaction and verified session-context boundary. Elasticsearch and a
  materialized projection remain deferred until measured reporting volume
  requires one.
* Organization finance is rooted at the economic tenant root and requires both
  corporate-credit-view and gift-card-view authority. Platform access requires
  both corresponding platform permissions. A subsidiary-only permission scope
  cannot expose root-wide totals.
* One stable opaque cursor orders cross-source events by server occurrence time
  and deterministic event key. Summary categories include granted, reversed,
  issued, distributed, corporate/card remaining, and terminal returned value;
  reserved, spent, and refunded remain Phase 4 categories.
* Reconciliation compares domain links, amounts, currencies, account roles,
  balances, terminal value, and orphan Phase 2 transactions with immutable
  Ledger postings. It returns deterministic findings and never changes state.
* Recipient reads require the current exact card owner. New SELECT-only Ledger
  RLS policies follow the already RLS-protected Gift Cards ownership row, and a
  claimant may read the full Distribution event history for their invitation.
  Existing organization/platform policies and every write restriction remain
  unchanged.
* Audit retains ownership of investigation queries. Dedicated
  `organization.audit.view` and `platform.audit.view` permissions protect
  stable, filtered history.
* Suspended organizations and inactive or terminal cards retain authorized
  historical visibility.

### Consequences

* Reporting can be discarded and rebuilt because it owns no authoritative
  persistence.
* Composite permission checks prevent a caller with only one source-data
  permission from inferring the other source.
* Reconciliation findings require an explicit, separately designed operational
  response; a read request can never silently rewrite financial evidence.
* Exact-owner history is available without company membership while
  PostgreSQL RLS still fails closed for another identity or missing context.
* A later projection, export, dashboard, or automated remediation workflow
  requires a new accepted task and must preserve Ledger authority.

---

## ADR-037 — Independent Frontend Client and Browser-Security Boundary

**Status:** Accepted
**Date:** 2026-07-28
**Blocks:** IMPL-017 and independent portal/recipient clients

### Context

Phase 1 and Phase 2 backend behavior is complete, while the Development console
is intentionally a technical demonstration rather than the long-term finance
portal or recipient experience. The two audiences have different workflows,
release cadences, and presentation needs. Browser clients also need an explicit
token boundary: enabling arbitrary cross-origin JavaScript and storing a
30-day refresh token in browser storage would unnecessarily expose the durable
session credential.

### Decision

* Keep this repository as the authoritative backend modular monolith. Build the
  platform/customer portal and the recipient gift-card application in two separate
  repositories.
* Keep the backend API as a JWT bearer resource server. It does not add broad
  CORS or a parallel cookie-authentication scheme for browsers.
* A production web deployment uses a same-origin BFF or trusted reverse proxy.
  The BFF holds/rotates backend tokens server-side and exposes an
  `HttpOnly`, `Secure`, appropriate-`SameSite` browser session. It must add CSRF
  protection to state-changing browser requests.
* Native mobile packaging may call the bearer API directly and store refresh
  credentials only in operating-system secure storage.
* OpenAPI is the client integration contract. Development Swagger remains
  available, while production exposure is a deployment decision.
* Add narrow discovery operations for current identity/context, the current
  user's own active memberships, and permission-gated Platform root-customer
  search. A picker result never grants authority: selecting an organization
  starts a new request carrying `X-Organization-Id`, and authentication
  re-verifies the active membership and tenant root.
* Identity-context discovery is SELECT-only under PostgreSQL RLS, requires the
  exact session user, and is disabled when an organization is already selected.

### Consequences

* Portal and recipient UI work can evolve independently without coupling
  backend module boundaries to a frontend framework.
* Refresh tokens do not need to enter browser JavaScript storage, and the API
  does not become broadly callable from arbitrary origins.
* BFF deployment and CSRF controls are required before a production browser
  release; they are not simulated inside this backend slice.
* Frontends can render navigation and organization selection from explicit,
  documented APIs instead of reverse-engineering JWT claims or relying on the
  developer console.
* Authorization, tenancy, and financial invariants remain server/database
  responsibilities regardless of what the frontend displays.

---

## ADR-038 — Authoritative Additive Financial-History Search

**Status:** Accepted
**Date:** 2026-07-29
**Blocks:** IMPL-018 and portal PORTAL-010

### Context

The portal needs a searchable organization transaction timeline. The existing
financial-history endpoint already owns the correct cross-module projection,
tenant-root boundary, composite finance permission, PostgreSQL RLS scope, and
stable cursor pagination. Filtering only in the portal would produce incomplete
pages and make browser code a competing financial authority. Replacing the
existing request contract would also create unnecessary churn for the
separately developed cardholder application.

### Decision

* Extend the existing organization financial-history operation with optional
  exact category, operation, and currency filters; a bounded literal
  case-insensitive reference filter; an inclusive UTC start; and an exclusive
  UTC end.
* Introduce a dedicated organization-history search request. Keep
  `ReportingPageRequest` and all cardholder reporting operations unchanged.
* Apply filters after the tenant-scoped financial-history union and before its
  deterministic newest-first pagination.
* Bind normalized filters to filtered cursor tokens. A cursor presented with a
  different filter set is invalid.
* Preserve the existing unfiltered request and cursor format so current callers
  remain compatible.
* Keep export outside this slice. Export format, privacy, formula-injection,
  audit, and retention requirements need a separate accepted decision.

### Consequences

* The backend remains the single authority for complete financial search
  results, permissions, tenant scope, and pagination.
* Existing unfiltered portal or cardholder-adjacent clients keep working; the
  cardholder application repository and cardholder API surface are untouched.
* Filter normalization and cursor fingerprints become part of the OpenAPI
  contract and require focused compatibility tests.
* A later export feature cannot infer policy from this read-only search
  operation and must be designed separately.

---

## ADR-039 — Cardholder Claim Session and Trusted Proxy Boundary

**Status:** Accepted
**Date:** 2026-07-29
**Blocks:** IMPL-019 and cardholder CARD-001 real-backend verification

### Context

The anonymous claim endpoint is consumed through an independent same-origin
cardholder BFF. Its current ten-per-minute source-IP quota sees only the BFF's
address, collapsing every recipient into one pilot-wide quota. A newly created
recipient also cannot transition directly into the authenticated cardholder
experience: the successful claim deliberately returns only a masked contact and
no token pair.

Returning the unmasked contact would weaken data minimization. Issuing a full
session for an existing identity from a passwordless claim would be worse:
possession of one invitation could unlock that identity's other cards without
their account password.

### Decision

* Return an optional token pair only when the successful claim created the
  recipient identity. Keep the masked contact and never return the unmasked
  email address or phone number.
* Issue the session through a narrow Identity contract after password
  verification, inside the same outer serializable transaction as identity
  creation, claim completion, ownership transfer, and audit.
* On an idempotent replay of a completed new-identity claim, verify the
  submitted password before issuing a fresh session.
* Existing-identity claims remain passwordless ownership activation and return
  no session; normal password login remains required.
* Honor `X-Forwarded-For` only when the immediate BFF/reverse proxy address is
  explicitly configured in the backend's known-proxy allowlist. Process at
  most one forwarded hop.
* Require the BFF to overwrite the outbound forwarding header from its verified
  connection address. Never trust or append browser-supplied forwarding data.
* Keep remote-address rate limiting as the default when no trusted proxy is
  configured.

### Consequences

* New recipients can enter a secure BFF session without retyping an identifier
  the backend intentionally masked.
* Existing accounts keep the stronger password boundary for access to their
  complete cardholder account.
* Pilot claim quotas can partition by actual client address without accepting
  spoofed forwarding headers.
* The cardholder BFF must make a small coordinated change: forward only its
  observed client address, consume the optional token pair server-side, and
  repin to the published OpenAPI.
* Existing clients remain compatible because the response addition and proxy
  configuration are optional.

---

## ADR-040 — Portal-Safe Team Administration Contract

**Status:** Accepted
**Date:** 2026-07-30
**Blocks:** IMPL-020 / PORTAL-012

### Context

The existing membership and role APIs preserve authorization and tenant
isolation, but they are not sufficient for a safe independent portal workflow:
membership responses expose only a global user UUID, membership creation
requires that UUID, and role assignments can be created but not listed. A
browser would therefore have to ask an administrator to paste technical
identifiers and could not reconstruct authoritative assigned access after a
reload.

The separately developed cardholder client does not use organization
membership or role-management endpoints. An additive organization-admin
contract can close these gaps without changing cardholder identity, claim,
card, or reporting behavior.

### Decision

* Keep the existing `userId` membership-creation input compatible and add an
  optional email input. Exactly one selector is required.
* Resolve an email only after the caller has
  `organization.memberships.create`; match one active email identity and return
  a generic not-found response otherwise. This is membership creation for an
  existing staff account, not account creation or invitation.
* Add a nullable staff email to membership responses. It may be returned only
  after the existing organization or platform membership-view authorization
  succeeds. Passwords, sessions, recipient phone identifiers, and normalized
  credentials are never returned.
* Add read-only listing of role assignments under the existing organization
  role path. Require `role.view`, preserve exact organization scope and forced
  RLS, and return stable created-time/UUID order.
* Keep role creation, additive permission grants, and scoped assignment writes
  on their existing endpoints and permissions.
* Make only additive OpenAPI changes and add no database migration.

### Consequences

* Portal users can select existing members and roles by backend-returned labels
  while the browser retains UUIDs only as non-rendered selectors.
* Authorized team viewers receive staff login email, which is personal data;
  the named membership-view permission and tenant boundary protect disclosure.
* Existing UUID-based callers remain compatible, and cardholder endpoints and
  response shapes remain unchanged.
* Creating a global user, sending an invitation, resetting a password,
  revoking permissions, revoking assignments, deleting roles, and validating
  `SelectedOrganizations` UX remain outside this contract.

---

## ADR-041 — Independent Client Contract Convergence

**Status:** Accepted
**Date:** 2026-07-31
**Blocks:** IMPL-021 and a shared portal/cardholder backend pin

### Context

IMPL-019 and IMPL-020 were independently published from IMPL-018. Each is
additive and deliberately leaves the other client's surface unchanged, but the
two commits are siblings: neither deployable revision contains both contracts.
They also independently assigned ADR-039 to different decisions.

### Decision

* Publish one convergence revision containing the exact additive behavior of
  both predecessor slices.
* Preserve a single scoped `AuthenticationService` instance for
  `IAuthenticationService` and `IRecipientClaimSessionIssuer`; register the
  permission-protected `IOrganizationStaffDirectory` independently.
* Add no new API, permission, persistence, migration, financial, ownership,
  tenant, or browser-session behavior during convergence.
* Keep cardholder claim session/proxy behavior as ADR-039, renumber portal-safe
  team administration to ADR-040, and record convergence as ADR-041.
* Require the complete unit, architecture, native-PostgreSQL, EF model, and
  combined OpenAPI gates before either frontend repins.

### Consequences

* The portal and cardholder applications can target one authoritative backend
  revision without losing either client's required contract.
* Existing consumers of the IMPL-018, IMPL-019, or IMPL-020 surfaces remain
  compatible because the combined contract is their additive union.
* Frontend repinning and release-branch integration remain separate repository
  tasks; Phase 3 and Phase 4 behavior do not enter this convergence slice.

---

## ADR-042 — Synchronized Phase 2 Release-Candidate Identity

**Status:** Accepted
**Date:** 2026-08-01
**Blocks:** RELEASE-001 and staging promotion

### Context

The backend, portal, and cardholder application are independent repositories.
Their completed Phase 2 commits work together, but a deployment cannot be
reproduced from a version in only one repository and no tag convention exists.

### Decision

* Apply the same SemVer pre-release label, `v0.2.0-rc.1`, to the verified commit
  in each repository and record the three exact commit hashes together.
* Treat the tag as a source-and-verification baseline, not evidence that
  external infrastructure or production notification delivery is complete.
* Require HTTPS client origins, server-side browser sessions, durable Data
  Protection keys, separate least-privilege databases, explicit one-hop proxy
  trust, health probes, and secret-free configuration before staging promotion.
* Keep backend domain, authorization, tenancy, ownership, financial, and audit
  rules unchanged during release packaging.

### Consequences

* Operators can identify one compatible three-repository deployment without
  pretending the repositories share a commit graph.
* A later candidate increments only the pre-release suffix and records a new
  commit triplet; the stable `v0.2.0` tag requires a successful real staging
  deployment and explicit promotion.
* Hosting, DNS/TLS, managed secrets, PostgreSQL endpoints, and notification
  provider selection remain external deployment inputs.

---

## ADR-044 — Payment-Provision Reservation Window

**Status:** Accepted
**Date:** 2026-08-04
**Blocks:** IMPL-027 payment provisions

### Context

ADR-033 introduced a time-bounded provision that reserves value before final
confirmation, but deliberately left its TTL undecided; `DOMAIN_RULES` §14 has
carried it as a deferred decision since. Provisions cannot be implemented
without it, because the window is what bounds how long a cardholder's value can
be held by a till that never confirms.

The tension is concrete. Too short and a legitimate sale fails mid-checkout —
the cashier calls a supervisor, the customer changes their mind about a line
item, the terminal reprints a receipt. Too long and an abandoned or crashed till
strands value the cardholder can see but cannot spend, with no way for them to
release it themselves.

### Decision

**A payment provision reserves value for 2 minutes, evaluated against the server
clock.**

* The window starts when the provision is created, not when its QR token was
  issued. The 60-second token TTL (ADR-017) bounds how long a code stays
  presentable; this bounds how long value stays held once a sale has begun.
* Expiry is derived from the server clock only. As with ADR-017, no client
  asserts a time, so no clock-skew allowance applies.
* Expiration releases the reservation without a Ledger posting (ADR-033) and is
  finalised by a PostgreSQL-coordinated sweep, so a provision is effectively
  expired at its deadline even before the sweep runs — the same shape as
  gift-card expiration under ADR-035.
* The value is configurable but validated on start, so an environment cannot
  silently widen the window.

### Consequences

* An abandoned sale returns value to the cardholder within two minutes without
  anyone intervening.
* A till that needs longer than two minutes must create a fresh provision, which
  requires a fresh QR credential — so a stalled sale is re-authorised rather
  than silently extended.
* The terminal retry window after a *failed confirmation* remains deferred. It
  is a different question: how long a till may re-present the same purchase
  identity after an ambiguous failure, which ADR-018 ties to the token rather
  than to this reservation.

---

## ADR-045 — Redemption Counter-Account and Settlement Representation

**Status:** Accepted
**Raised:** 2026-08-05 (preparing IMPL-028)
**Accepted:** 2026-08-05
**Blocks:** IMPL-028 redemption confirmation

### Context

Confirming a provision posts the redemption transaction (ADR-018). Every posting
balances (ADR-014), so the debit against the gift-card value account needs a
counter-account, and the Ledger has only three account types today:

```text
PlatformFunding                one per currency, touched only by allocation
OrganizationCorporateCredit    one per root customer per currency
GiftCardValue                  one per card
```

None of them represents value that has been honoured at a till. This is a
financial-accounting decision, so it must be accepted before implementation
rather than inferred from whichever account is convenient.

The choice also determines what refund and reversal debit later (IMPL-029), and
what the Phase 4 store, terminal, and receipt reporting on the roadmap can
reconcile against.

### Options

**A. Credit the existing platform funding account.** No new account type, no
migration. But that account currently means "value the platform operator has not yet allocated to
a customer", and redeemed value is not available to allocate again. Allocation
and redemption would become indistinguishable in the same balance, and there
would be no single figure for value honoured.

**B. A new `PlatformRedemptionSettlement` account type, one per currency.**
Debit the card, credit settlement. Its balance is exactly the value redeemed and
owed to or settled with stores, which reconciliation can check directly against
the sum of redemption transactions. Refund later debits settlement and credits
the card, which is the natural inverse. Costs one account type, one migration,
and one new account per currency.

**C. A settlement account per store or per POS client.** Everything B gives,
plus per-store balances for the reporting the Phase 4 roadmap anticipates. Store
identity already lives on the terminal, so the key exists. Costs many more
accounts, and a store's identity becomes a financial-account dimension, which is
hard to change later.

### Decision

**Option B.** A new `PlatformRedemptionSettlement` account type, one account per
currency, created on first use exactly as the platform funding account is.
Redemption debits the gift-card value account and credits settlement.

It is the smallest change that keeps "not yet allocated" and "already honoured"
as separate, meaningful balances, and it gives refund an obvious inverse.
Per-store reporting is better served by aggregating over redemption
transactions, which already carry store and terminal references, than by
fragmenting the account model before a store-settlement requirement is real.
Option C remains available later because it is additive over this one.

The settlement balance exists to be **checked, not consulted**. Its purpose is
that it must always equal the sum of posted redemptions minus posted refunds; a
divergence is evidence of a defect, which is the reason this account is
preferred over reusing platform funding.

### Consequences

* Redeemed value never returns to the allocatable pool. Nothing moves settlement
  back into platform funding, because a customer's purchase does not restore
  the platform operator's capacity to allocate. Refund and reversal (IMPL-029) debit settlement
  and credit the card, never platform funding.
* Reconciliation gains one deterministic check: settlement balance per currency
  equals the sum of `gift_card.redemption` postings less refund postings. Like
  every other finding it is read-only and never repairs anything (ADR-036).
* Per-sale attribution is not this account's job. Which organization, cardholder,
  POS client, terminal, store, amount, and time is already recorded on the
  provision row and is linked from the redemption transaction.
* Ledger privileges are unchanged. Posting stays insert-only for the runtime
  role, so settlement history is as immutable as everything else (ADR-019).
* Settlement accounts carry no `organization_id`. They are platform-scoped, like
  platform funding, and reachable only through the controlled platform RLS path.

---

## ADR-046 — Confirmation Amount and Terminal Retry Window

**Status:** Accepted
**Raised:** 2026-08-05 (preparing IMPL-028)
**Accepted:** 2026-08-05
**Blocks:** IMPL-028 redemption confirmation
**Closes:** the terminal retry window deferred in `DOMAIN_RULES` §14

### Context

Two questions about the confirmation contract that ADR-018 deliberately did not
settle. `DOMAIN_RULES` §14 has carried the second as deferred since Phase 4
planning, and ADR-044 explicitly pushed it here.

**Does confirmation charge exactly the provisioned amount?** Real counter flows
adjust after the hold: a line item is voided, a coupon is applied, the total
comes in under the amount the till reserved. If confirmation must match the
provision exactly, a till has to cancel and re-provision, which needs a fresh QR
credential from the customer's phone. If it may be less, the difference is
released and the contract carries a second amount that must be bounded by the
provision.

**How long may a till re-present the same purchase after an ambiguous
failure?** The network drops after commit but before the response; the terminal
reboots mid-sale. The till must be able to ask again.

### Options

*Amount:* exact provisioned amount only; or any amount from zero exclusive up to
the provisioned amount, releasing the remainder.

*Retry:* a bounded window after which the same credential is refused; or no
window at all.

### Decision

**Confirmation may charge any positive amount up to and including the
provisioned amount.** The hold is a ceiling on what the till may take, and that
ceiling is what protects the cardholder. Charging less than it takes nothing
extra from them, while forbidding it would force a fresh scan of the customer's
phone for an ordinary voided line item. An amount above the provisioned amount
is refused; the till must create a new provision, which requires a new
credential, so a larger charge is always re-authorised rather than silently
extended.

**There is no retry window.** Idempotency is derived from the server-issued
credential (ADR-018), and that credential maps to exactly one redemption
permanently. A retry presenting it returns the original outcome whether it
arrives in two seconds or two days, so allowing it risks nothing. A window could
only convert a safe repeat into a failure, would have to be judged against a
clock the till does not control, and would leave a disconnected terminal with no
way to learn whether its customer was charged.

### Consequences

* The confirmation request carries an explicit amount. Omitting it is not the
  same as confirming the full hold: a POS must state what it is charging, so a
  defect in the till cannot silently take the ceiling.
* A partial confirmation releases its remainder in the same transaction that
  posts the redemption. The provision becomes `Confirmed` as a whole; there is
  no partially-consumed state, because a second charge against the same hold
  would breach the single-use guarantee of ADR-017.
* Exactly the confirmed amount is posted. The provisioned amount remains on the
  row as the authorised ceiling and is what reconciliation compares against.
* `DOMAIN_RULES` §14 no longer defers a terminal retry window.
* Tip adjustment, incremental authorisation, and split tender are not implied by
  this decision. Each would need its own accepted change, because all three ask
  for a charge above an existing hold, which this explicitly refuses.

---

## ADR-047 — Partial-Refund Accumulation and Over-Refund Protection

**Status:** Accepted
**Raised:** 2026-08-05
**Accepted:** 2026-08-05 by product owner
**Blocks:** IMPL-029 refunds

### Context

A redemption may cover several purchased items while a customer returns only
some of them. Full-refund-only behavior would force the till either to return
too much value or refuse an ordinary retail return. Supporting partial refunds
means the platform must also prevent concurrent requests from cumulatively
returning more than the confirmed charge.

### Decision

Refunds are immutable positive operations against one confirmed redemption.
Multiple partial refunds are permitted while their cumulative posted amount is
less than or equal to the provision's `confirmed_amount`. Each request carries
an idempotency key scoped to that redemption: same key and same normalized
intent return the original row, while changed intent conflicts.

The caller-chosen key is not the financial safety boundary. The refund workflow
serializes on the original provision/card and derives remaining refundable
value from immutable posted refunds inside the same transaction that posts the
next refund. This locked cumulative ceiling is what prevents two different keys
from over-refunding concurrently.

### Consequences

* A refund row and Ledger transaction are append-only; the redemption remains
  unchanged.
* Refund currency is inherited from the redemption and cannot be selected by
  the POS.
* The accounting movement is ADR-045's inverse: debit settlement and credit the
  original card, never corporate credit.
* Fully refunded is derived from cumulative refund value rather than a mutable
  state that could disagree with Ledger history.

---

## ADR-048 — Refund Lifecycle and Reversal Authority

**Status:** Accepted
**Raised:** 2026-08-05
**Accepted:** 2026-08-05 by product owner
**Blocks:** IMPL-029 runtime scaffolding

### Context

ADR-045 fixes where value moves but does not say what happens when the original
card is suspended, expired, or cancelled, whether an initial refund has a time
window, or whether “reversal” is a separate platform-operator correction. A
refund that credits an expired/cancelled card would violate the delivered
terminal-card zero-balance invariant; an alternate payout or replacement card
would introduce a new product boundary.

### Decision

* Permit refunds without an arbitrary time window while the original card is
  `Active` or `Suspended`. Suspension prevents spending, not an immutable
  compensating credit.
* Refuse refunds to `Cancelled` or `Expired` cards. Replacement-card, cash, bank,
  or corporate-credit payout requires a separate accepted design.
* Only the POS client that owns the original provision may refund it. Terminals
  under that client share the authority; unrelated POS clients receive
  not-found.
* For IMPL-029, a reversal means refunding the entire remaining refundable
  amount through the same contract. A platform-operator correction command and
  permission are deferred because they grant materially broader authority.

### Consequences

This keeps IMPL-029 inside existing card, POS, RLS, and Ledger authorities. It
does not claim that terminal-card refunds are unnecessary; it records that they
need an explicit destination rather than silently breaking lifecycle invariants.

---

## ADR-049 — POS Payment Reporting Authority and Source

**Status:** Accepted
**Raised:** 2026-08-05
**Accepted:** 2026-08-05 as the approved IMPL-030 direction
**Blocks:** IMPL-030 reporting

### Context

Phase 4 requires store, terminal, receipt, payment, refund, and reversal
reporting. Payment provisions and refunds already retain immutable tenant, card,
POS client, terminal, store, receipt reference, amount, currency, time, and
Ledger attribution. Creating a second projection before a measured scale need
would add consistency and recovery work, while customer access to all stores
would cross the established tenant boundary.

### Decision

IMPL-030 is a PostgreSQL-backed, read-only platform report protected by the
dedicated `platform.payments.view` permission. It reads authoritative Payments
rows inside the normal transaction-local RLS context. Customer-organization
members retain their existing tenant financial history and do not receive this
cross-tenant operational view; POS principals receive no reporting authority.

The store dimension remains the normalized `store_reference` captured from the
registered terminal. Receipt detail is a derived view of one provision, its
confirmation, and ordered refund rows. A reversal is reported when immutable
cumulative refunds equal the confirmed amount. Cursor pagination is stable and
bound to the normalized filter set.

### Consequences

* PostgreSQL remains the sole reporting and financial authority; Elasticsearch
  and materialized projections remain deferred until measured demand justifies
  them.
* The built-in Platform Administrator receives the new permission through the
  synchronized permission catalogue; narrower platform roles may receive it
  independently from POS-client management authority.
* Reports expose public card and business references but not owner identity,
  raw payment credentials, POS secrets, refund idempotency keys, or audit-only
  metadata.
* A future first-class Store aggregate or per-store settlement model must be an
  explicit migration, not inferred from this reporting view.

---

## ADR-050 — Human-Enterable Numeric Payment Credential

**Status:** Accepted
**Raised:** 2026-08-05
**Accepted:** 2026-08-05 by product owner
**Blocks:** IMPL-031 and CARD-006

### Context

ADR-017 says one opaque payment credential may be displayed as QR or numeric,
but the delivered 256-bit QR value needs roughly 78 decimal digits. Presenting
that representation for manual till entry is not usable. A shorter alias has
less entropy and therefore needs explicit format, lookup, rate-limit, storage,
and replay boundaries rather than a client-derived truncation.

### Decision

Every new payment token receives an independent CSPRNG 12-digit decimal alias.
The cardholder renders it as three groups of four; canonical form is exactly 12
ASCII digits. Spaces and hyphens are accepted only as input separators. The raw
code is returned once with the QR token, while PostgreSQL stores only its
SHA-256 hash under a unique index.

The code and QR token refer to the same payment-token row, 60-second expiry,
and irreversible consumption stamp. A provision request supplies exactly one
form. Numeric lookup is available only to an authenticated, rate-limited POS
principal and receives exact hash-scoped RLS visibility solely to resolve the
token ID. The existing token-ID candidate then governs card, reservation, and
Ledger processing. Unknown, expired, consumed, and replayed codes remain one
generic refusal.

### Consequences

* Six digits are rejected as too small for a payment credential at the target
  scale. The 12-digit alias has one trillion values and is still practical for
  grouped manual entry.
* Random-code collisions are handled by a unique hash and conflict-safe retry;
  they never become an ambiguous lookup or issuance failure visible to users.
* Consuming either presentation consumes both, so QR and numeric races still
  yield exactly one provision.
* Legacy rows may have a null numeric hash during upgrade. Their 60-second QR
  validity is unchanged; every post-upgrade issuance has both forms.
* The numeric alias is not a PIN, card number, offline code, or reusable secret.
  It must not be logged, audited, persisted in a client, or given a longer TTL.

---

---

## ADR-051 — Asynchronous Bulk Gift-Card Batches

**Status:** Accepted
**Date:** 2026-08-09
**Blocks:** IMPL-034 and the portal spreadsheet upload
**Supersedes:** the synchronous 100-item ceiling of ADR-030, for bulk only

### Context

ADR-030 fixed bulk issuance as synchronous, all-or-nothing, and capped at 100
items. That was the right call for an API caller assembling a request by hand.
It stops being the right call the moment a company uploads the spreadsheet it
already keeps, because those routinely carry several hundred to a few thousand
employees, and a payroll-shaped file is the actual use case the product exists
to serve.

Three ways to carry a 1,500-row file were considered.

**Chunk it in the browser.** Costs nothing in the backend. It also destroys the
guarantee ADR-030 existed to protect: chunk seven fails after chunks one to six
have already issued cards, debited corporate credit, and sent activation
emails. There is no batch record for the upload as a whole, so nobody can say
afterwards what happened, and the failure surfaces as a partially completed
payroll.

**Raise the ceiling and stay synchronous.** Preserves atomicity, and preserves
it by holding one `SERIALIZABLE` transaction open while posting thousands of
Ledger transactions. The request outlives ordinary HTTP timeouts, the lock
window grows with the file, and a serialization conflict at row 1,900 discards
all of it. Workable to perhaps 300; not to 2,000.

**Process it asynchronously.** Accepted.

### Decision

A bulk batch is accepted, persisted, and answered immediately with its
identifier and a `Pending` state. A background processor then works it in
bounded chunks, and the caller polls batch status.

* **The batch is durable before the response is sent.** The complete normalized
  intent for every row is persisted in the accepting transaction. Nothing is
  held in memory, so a restart mid-file resumes rather than losing the upload.
* **Atomicity moves from the file to the row.** Each item keeps the existing
  single-card issuance and distribution path, with the deterministic
  idempotency key already derived from the batch key and item reference, so it
  commits or does not commit on its own. This is the real cost of this decision
  and it is deliberate: a 2,000-row file cannot be one transaction, so the
  honest model is per-row atomicity plus a complete, queryable per-row outcome.
* **A failed row never blocks the rest.** It is recorded with its failure code
  and processing continues. The batch reaches `Completed` when every row has an
  outcome, whether or not every row succeeded, and the summary states how many
  of each. `Completed` therefore means finished, not successful.
* **Notification stays post-commit and per row**, delivered through the
  Notifications outbox, so an activation link exists only for a card that was
  actually issued.
* **Retry is by batch and replays only unsuccessful rows**, keyed by the same
  deterministic item keys, so a retry cannot double-issue a row that already
  succeeded. A completed source remains immutable; retry creates at most one
  child batch containing only its failed rows, and a repeated retry request
  returns that child.
* The synchronous path and its 100-item ceiling remain for existing callers.
  This decision adds a second mode; it does not remove the first.

### Consequences

* A partially successful batch becomes a normal, representable outcome. The
  portal must show per-row results rather than one success or failure, and the
  operator needs a way to act on the failures. Reporting an aggregate alone
  would misrepresent what happened to somebody's payroll.
* Corporate credit is consumed progressively rather than at once, so a file
  larger than the available balance issues cards until the balance runs out and
  records the remainder as failed. The portal must compare the file total with
  the balance before submitting, and the failure code must say so plainly.
* Acceptance does not reserve corporate credit. Each row competes for the
  posted balance through the existing serialized issuance path when it is
  processed. Concurrent batches may therefore complete with different mixes of
  succeeded and insufficient-credit rows; the durable per-row outcomes are the
  authoritative result. Adding a corporate-credit reservation account and its
  release/expiry lifecycle is a separate financial design, not part of
  IMPL-034.
* Batch state and item results stay tenant-scoped under forced RLS like every
  other Distribution record, and item results stay append-only.
* Throughput, chunk size, and poll interval are validated configuration, not
  domain rules.
* A transient serialization or optimistic-concurrency conflict is not a
  business outcome. The worker leaves that row `Pending` for a fresh pass;
  only mapped non-transient `AppException` failures settle it as `Failed`.

---

## ADR-052 — Organization Card Visibility After Claim

**Status:** Accepted
**Date:** 2026-08-09
**Blocks:** IMPL-033 organization card register

### Context

An organization currently cannot see a card it funded once that card has been
distributed: the inventory query filters on `OrganizationInventory` ownership,
so a card leaves the company's view at the moment it becomes useful to the
recipient. Companies need a register of what they have issued, for support, for
reconciliation, and simply to answer "did she get it".

The obvious implementation lists every card with its live balance. That is a
different product from the one it appears to be. A claimed card belongs to an
identity, not to the company, and its remaining balance is a running record of
what that person has and has not spent. Publishing it to anyone holding
`organization.gift_cards.view` turns an employee benefit into a spending
monitor, which no employee consented to.

The countervailing argument is real: the company funded the card and receives
the remainder back on cancellation or expiry, so it has a genuine financial
interest in the value outstanding.

### Decision

The organization card register lists every card the organization funded, in
every lifecycle and ownership state, and discloses **what the company put in,
not what the person has left**.

Per card it returns the public reference, lifecycle and ownership state, funded
amount, currency, validity window, issuing organization, and the distribution
timestamps. The recipient contact is masked, exactly as it already is in
staff-facing distribution and sharing history.

It does not return the Ledger-derived current balance, reserved value, or any
transaction history for a card in `IdentityOwned` ownership. For a card still
in `OrganizationInventory` or `AwaitingClaim` there is no such tension: nobody
else owns it, so its balance is the company's own money and is returned.

The company's aggregate financial position is unaffected and already available.
The existing financial summary reports outstanding value per currency across
the tenant, which is the figure finance actually needs, without attributing
spending to a named person.

### Consequences

* An organization can answer "what did we issue, to whom, and is it still
  live", which it could not before, without acquiring the ability to watch
  individuals spend.
* "How much is left on my card" is answered by the cardholder, who can already
  see it, or by a platform operator, who already holds cross-tenant
  authority for exactly this reason.
* Should per-card balance visibility for claimed cards ever be required, it
  needs its own named permission and its own decision. It must not arrive as a
  new field on this response.
* The register is read-only and composes existing authoritative records under
  ADR-036. It owns no schema and no projection.

## Decision Record Template

Copy this section when adding a new decision:

```md
## ADR-XXX — Decision Title

**Status:** Open | Proposed | Accepted | Superseded | Rejected
**Date:** YYYY-MM-DD
**Blocks:** Relevant task or phase

### Context

What problem requires a durable decision?

### Options

What options were evaluated?

### Decision

What was selected?

### Consequences

What becomes easier, harder, required, or forbidden?

### Supersedes

Optional previous ADR reference.
```

---

## ADR-053 — E-pin Reseller Partners as Tenant-Owned Minting Clients

**Status:** Accepted
**Date:** 2026-08-13
**Blocks:** PARTNER-001 partner registry, and the credential exchange, minting,
and orphan claim slices that follow it

### Context

A second customer shape is arriving. E-pin resellers such as BynoGame, Eneba,
and Kabasakal sell gift cards from their own checkout. They will never use the
portal, they call an API at purchase time, and they do not know who the buyer
is: the buyer is their customer, not ours. What they need back is a redeemable
product they can hand over, not a card addressed to a named recipient.

Every issuance path we have assumes a human organization member behind a JWT,
and every claim path assumes the sender knew the recipient's contact when the
card was created. Neither holds here.

The commercial risk drives the design. A reseller API key is a minting
credential. If one leaks, or a reseller's own staff turns bad, the failure mode
is someone generating high-value codes and liquidating them before anyone
notices. Whatever we build has to make that bounded and reversible.

The POS client (ADR-043) is the closest existing pattern: a registered machine
identity with a hashed secret that exchanges credentials for a short-lived
token. But a POS client is deliberately platform-scoped, carries no
organization, and has no funding authority at all; `SetPosClient` nulls the
tenant fields precisely so RLS fails closed. A reseller must be able to move
money, so it cannot be modelled the same way.

### Decision

**A partner is a root organization.** The reseller is onboarded through the
existing organization path and funded with prepaid corporate credit. Minting
debits that balance, so a compromised credential can never produce more value
than the reseller has already paid for. That prepaid float is the primary
control, and it is a hard bound rather than a policy check.

The `partners` module owns a thin registry keyed by the funding root
organization: `partners.partners` and `partners.api_clients`. Both are
**tenant-owned and carry forced RLS from their first migration**, keyed on the
funding root. This is the deliberate difference from `payments.pos_clients`,
which has no policy only because the platform operator owns the tills and there is no
organization to isolate on.

**The credential exchange gets a narrow, flagged, read-only escape rather than
an RLS exemption.** Resolving a credential must happen before any caller is
authenticated, which a purely tenant-scoped policy would make impossible.
Dropping RLS on the table to solve that would trade a permanent hole for a
transient need. Instead the policy admits
`app.is_partner_credential_lookup`, set only on an independently scoped
connection by the credential exchange. It appears in `using` and deliberately
not in `with check`, so the anonymous path can read a partner and a key and can
never create or modify either.

One partner per funding tenant, enforced by a unique index. Two partner rows on
one organization would make the corporate-credit balance a shared pool with no
way to attribute a mint, and would defeat per-key velocity accounting.

Only secret hashes persist, and neither table grants runtime DELETE: a retired
credential is evidence, and a partner row anchors the funding tenant of every
card it ever minted. Identity, funding tenant, and code are immutable by
trigger, because repointing `root_organization_id` would silently move future
minting onto another company's money and rewriting a code would repoint a live
integration.

Retirement is by disabling, at either level. Disabling stops new minting but
deliberately does **not** invalidate e-pins already sold; those belong to the
reseller's buyers, and voiding them indiscriminately would punish customers for
their supplier's compromise. Voiding is a separate, deliberate clawback scoped
to unclaimed cards.

### Consequences

The existing ledger, RLS, permission, audit, reporting, and reconciliation
machinery applies to partner minting unchanged, which is the main reason for
anchoring a partner to an organization rather than inventing a parallel
funding concept.

A partner client still has no `OrganizationMembership`, so it cannot use
`IOrganizationPermissionAuthorizer`. Authority will come from explicit scopes on
the client row evaluated by a dedicated authorizer, and
`LedgerWriter.RecordGiftCardIssuanceAsync` will need a third branch in its
authority gate alongside the existing verified-member and accepted-bulk-processor
branches. That is a new hole in the gate guarding every mint and is the riskiest
part of the work; it is deliberately deferred to its own reviewable slice.

Velocity and face-value caps are not modelled here. They are rate-limit
counters, not balances, so they do not belong in the ledger; they arrive with
the minting slice.

There is no metrics or alerting infrastructure in the platform, so within its
caps a compromised key mints quietly until someone runs the reconciliation
report. This is a known and accepted residual gap, recorded here so it is not
mistaken for an oversight.

---

## ADR-054 — Partial Approval Is Opt-In Per Request

**Status:** Accepted, 2026-08-20
**Context:** POS strategy research (`docs/POS-STRATEGY.md` §1.3, §2.2)

A hold larger than the card's available value was refused outright with
`payment.provision.insufficient_value`. Research into how tills actually accept
gift cards showed this is backwards for the dominant case: a gift card usually
cannot settle a whole basket, the customer rarely knows the remaining balance,
and retail systems expect either a planned split tender or a reactive partial
approval, where the card is approved for less than was asked and the remainder
is collected by another tender. As written, a cashier could only succeed by
guessing an amount at or below a balance nobody had told them, and the platform
offered no way to ask.

**Decision.** `CreatePaymentProvisionRequest` carries `AllowPartialApproval`,
defaulting to **false**. When it is false the previous refusal is unchanged. When
a till sets it and the card holds something but not enough, the hold is taken for
what is actually available and the response states what is still owed.

The default matters more than the feature. A caller written before this existed
reads a 201 as "approved for what I asked". Approving it for less without its
knowledge would silently under-charge every sale. Making the till declare that it
can handle a partial answer mirrors card networks, where a terminal must signal
support for partial authorization before an issuer will give one.

**Consequences.**

- `PaymentProvisionResult` gains `RequestedAmount` and `OutstandingAmount`.
  Outstanding is stated rather than left to the caller's arithmetic, because
  getting it wrong means a customer walks out having underpaid.
- `payment_provisions` gains `requested_amount`, backfilled from `amount`, since
  every existing provision was necessarily a full approval. It is stored rather
  than derived because a later read cannot otherwise tell that a hold of 30 was
  the answer to a question about 50, which reconciliation and disputes need.
- The domain refuses `amount > requestedAmount`. That is the only way this
  feature could take money that was never requested.
- Confirmation is unchanged: the held amount is still the ceiling, so a partial
  approval cannot be confirmed for the full sale total.
- A partial approval reserves only the approved amount, so abandoning the sale
  leaves the rest of the card spendable.

**Rejected:** approving partially by default. It is the friendlier behaviour and
the wrong one, because the failure mode is silent under-charging in someone
else's integration.

---

## ADR-055 — A POS Device Proves Its Audit Scope With The Card It Is Holding

**Status:** Accepted, 2026-08-20
**Supersedes part of:** the write rule added with `audit_records_pos_provision_write`

### Context

A POS token deliberately carries no user, organization, or tenant scope, and the
authentication handler refuses any attempt to select one. Audit records are
tenant-scoped and `audit_records` has FORCE row-level security, so a device with
no tenant needs some other proof before it can write one.

The proof in place was `audit.caller_pos_client_holds_scope`, which asks whether
the calling POS client already holds a provision funded by the target
organization. It works for the operations it was written for, because the
provision row is inserted before the audit record is written.

Adding balance inquiry exposed its shape. An inquiry reserves nothing, so it
writes no provision, so on first contact with an organization there is nothing
for the function to find and the audit write is refused with `42501`. Every
future POS operation that does not reserve value would meet the same wall:
activation, top-up, void and reversal are all in that class. The failure appears
at runtime as a policy violation with no obvious link to the new operation.

Two further things were true of that function and are worth recording. It is
defined in an **Audit** module migration and reads `payments.payment_provisions`,
so the audit schema depends on another module's table. The architecture tests
enforce module boundaries for C# only, so this crossing is invisible to them. It
also carries `exception when undefined_table then return false`, which is a
defensive acknowledgement of exactly that dependency.

### Decision

The application publishes the organization a device's presented credential
resolves to, as the transaction-local setting
`app.pos_credential_organization_id`, at the moment the credential is resolved to
a card. The audit write policy accepts **either** that setting **or** the
existing provision-based proof.

Additive on purpose. Nothing that passed before stops passing, provisioning is
untouched, and the change is reversible by restoring one policy.

### Why this is not a weaker check

Re-deriving from a table is stronger than trusting a session setting, and that
trade is real. But `app.user_id` and `app.organization_id` are set exactly this
way, from verified server state, by the same writer, inside the same
transaction, and the entire tenant boundary already rests on them. Trusting one
more setting from the same source is consistent with the model rather than an
exception to it; the cross-schema function was the exception. Keeping both
proofs means nothing is lost if that reasoning is ever judged wrong.

### Consequences

* Any future POS operation is auditable as soon as it resolves a credential. No
  migration teaching an audit function about another module's internals.
* The setting is transaction-local, so it cannot outlive the request that set it.
* The policy stays INSERT-only. A till still cannot read audit history.
* The cross-schema dependency remains, now documented rather than silent. Worth
  considering whether the architecture tests should assert something about SQL
  objects crossing schema boundaries: that rule is enforced for C# only today,
  and this is evidence the gap is real rather than theoretical.

---

## ADR-056 — Taking A Hold Requires An Idempotency Key

**Status:** Accepted, 2026-08-20

### Context

A payment credential is single use. If `POST /pos/payment-provisions` succeeded
and its response was lost, the credential was already consumed, the till never
learned the provision id, and it could not cancel a hold it could not name. The
customer's value stayed reserved until the two-minute window expired. Retrying
was worse than useless: an identical request is indistinguishable from a replay,
so it was refused, which is right for an attacker and wrong for a cashier whose
network dropped mid-sale.

This is the most common failure in real till integrations and the platform had no
answer to it.

### Decision

`CreatePaymentProvisionRequest` carries a required `IdempotencyKey`, unique per
POS client. A retry carrying the same key returns the original hold. A key reused
with different intent, meaning a different credential, requested amount, or POS
transaction reference, is a `payment.provision.idempotency_conflict`.

The check runs immediately after the credential row is locked and **before** the
credential is examined or consumed, so a retry never touches it.

Required rather than optional, matching refunds, which already use
`NormalizeRequired`, and matching the standing invariant that every financial
operation is idempotent keyed by operation type and idempotency key with a
database unique constraint. Optional would have meant integrators omit it and
meet the exact failure this removes.

### Consequences

* Uniqueness is `(pos_client_id, idempotency_key)`. Two shops choosing the same
  key is not a collision, and a client replaying its own key is the retry this
  exists to answer.
* Existing rows are backfilled with their own id. They predate the key and would
  otherwise all share the empty default and collide on the unique index.
* The demonstration seed uses a stable key rather than a generated one, which is
  what its idempotency requires: a second run is answered with the first run's
  hold instead of taking another.
* A request without a key is refused with 400 before any credential work, so the
  requirement is discoverable at the boundary rather than at runtime.


---

## ADR-057 — Audit Checkpoint Custody Is An Explicit Provider Choice

**Status:** Accepted, 2026-08-25

### Context

Checkpoint signing and witness publication were selected by hosting
environment: Development got local key files, and everything else refused to
enable checkpointing at all. That made the safe path also the only path, so no
deployment could turn checkpointing on. ADR-013 assumed a managed signer would
eventually exist; it did not.

Inferring custody from `IHostEnvironment` is also the wrong input. The question
is where the private key lives, not what the environment is named.

### Decision

`Audit:Checkpoints:Provider` names the adapter, and enabling checkpointing
without naming one fails startup. Two values are accepted.

`DevelopmentFile` keeps the existing local key and create-only directory, and
is still refused outside Development.

`RemoteHttp` calls a custody gateway that this project does not implement or
prescribe. The gateway holds an ECDSA P-256 key in KMS/HSM custody and writes
manifests to WORM storage. The application authenticates with a client
certificate over mutual TLS and never receives the private key.

The transport refuses anything it cannot verify: a signer response naming a
different algorithm or key identifier, a signature that is not 64 bytes of
P1363, a public key that is not P-256 SPKI, plain HTTP, redirects, URLs
carrying credentials or a query or fragment, oversized bodies, and malformed or
duplicated witness references. Publication is create-only through
`If-None-Match` with an idempotency key and a content digest; on 409 or 412 the
stored bytes are read back and compared in constant time, so an existing
checkpoint can never be rewritten with different content.

Remote response bodies never reach an exception message or a log.

### Consequences

* Custody is a deployment decision recorded in configuration, not a property of
  the environment name. An operator can run `RemoteHttp` against a staging
  gateway without pretending to be Production.
* The gateway is a new external dependency on the sealing path. A gateway
  outage delays sealing and raises worker event 1901; it does not refuse
  business writes, which matches the existing signer-outage behaviour.
* This project now specifies a wire contract it does not own. A gateway that
  answers differently is rejected rather than trusted, which is the intended
  direction, but it does mean the contract has to be documented for whoever
  builds the gateway. `docs/DEPLOYMENT.md` carries it.
* Mutual TLS cannot be proven by any test here. The adapter is covered against
  a stubbed transport only, and the handshake is a named-environment drill.
* Windows Schannel cannot present a client certificate whose private key is in
  an ephemeral key set, so PKCS#12 loading selects a container-backed key set on
  Windows and keeps the key in memory elsewhere. `PersistKeySet` is deliberately
  not used, so the container stays temporary and is removed when the certificate
  is disposed.
