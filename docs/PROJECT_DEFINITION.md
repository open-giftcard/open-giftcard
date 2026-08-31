# Project Definition: Digital Corporate Gift Card Platform

## Document Purpose

This document defines the business purpose, scope, technical direction, security
requirements, and phased roadmap of the Digital Corporate Gift Card
Platform.

It is the primary project-context document for anyone working in this
repository.

Sections marked **[OPEN DECISION]** must be resolved before implementation work
that depends on them begins.

Security-sensitive, financial, multi-tenant, and database architecture
decisions are not resolved in passing. Raise them, decide them deliberately,
and record the outcome in `docs/DECISIONS.md`.

---

## 1. Project Summary

Build a secure, multi-tenant platform that digitizes corporate gift cards.

Organizations currently buy physical retail gift cards in bulk and distribute
them manually to employees during holidays, company events, rewards, bonuses,
and incentive programs.

This platform replaces that physical workflow with:

* Corporate customer and organization management
* Subsidiary organization management
* Employee and user management
* Digital gift card issuance and distribution
* Secure balance management through an append-only ledger
* Gift card and partial-balance sharing
* Secure share links
* Dynamic, short-lived QR codes for POS redemption
* Organization-scoped roles and permissions
* Full auditability of financial and administrative actions

Target scale:

* Thousands of corporate organizations
* Millions of digital gift cards
* Concurrent gift card distribution operations
* Concurrent POS redemption requests
* Strict tenant isolation
* Strict financial consistency

---

## 2. Business Context

The platform operator sells corporate gift card value to companies.

Example:

Company A pays the platform operator 4,500,000 TL as part of a commercial agreement.

The platform operator may allocate 5,000,000 TL of gift card credit to Company A.

Company A then divides that corporate credit and distributes digital gift cards
to its employees.

The company may:

* Create individual gift cards
* Perform bulk distributions
* Assign different values to different employees
* Delegate gift card distribution authority to HR or Finance users
* Track distributed and remaining corporate value

An employee may later be allowed to divide their gift card balance and share
part of it with family members or other recipients.

At checkout, the end user displays a temporary QR code or numeric payment code.

The POS scans or enters the code.

The platform validates the payment credential and deducts the purchase amount
from the user's available digital gift card value.

---

## 3. Reference Business Flow

1. Company A purchases 5,000,000 TL of gift card credit.
2. A platform administrator creates Company A in the platform.
3. The platform operator assigns the initial Company Administrator.
4. The Company Administrator creates or invites HR, Finance, and other internal users.
5. The administrator creates organization-specific roles.
6. The administrator assigns selected permissions to those roles.
7. HR or Finance creates and distributes digital gift cards.
8. Employees receive an email/phone invitation and may claim and activate a gift
   card even when they had no account before delivery.
9. Employees may transfer part of their balance to another person when policy allows.
10. A recipient may receive the shared value through an account invitation or secure link.
11. During shopping, the client generates a dynamic QR or numeric payment code.
12. The cashier scans or enters the code at the POS.
13. The POS submits the payment credential and purchase amount.
14. The platform validates the credential, balance, gift card state, and POS identity.
15. The value is deducted through a concurrency-safe ledger transaction.
16. The POS receives an idempotent success or failure response.
17. The operation becomes visible in the appropriate transaction and audit histories.

---

## 4. Project Goals

The platform must provide:

* Secure digital corporate gift card management
* Multi-tenant organization isolation
* Organization and subsidiary hierarchy
* Fine-grained permission-based authorization
* Secure user and session management
* Financial transaction integrity
* Immutable financial history
* Full administrative auditability
* Scalable gift card distribution
* Secure balance sharing
* Dynamic POS payment credentials
* Concurrency-safe redemption
* Idempotent financial operations
* Future reporting and analytics support

---

## 5. Non-Goals for the Initial Foundation

The first implementation phase is not intended to deliver the complete
gift card platform.

The initial foundation must not prematurely implement:

* Corporate credit allocation
* Wallets
* Ledger entries
* Gift card issuance
* Gift card distribution
* Gift card sharing
* Secure share links
* Dynamic QR codes
* POS redemption
* Elasticsearch reporting
* Push notifications
* Mobile applications
* Microservices

These features will be introduced incrementally after the organization,
identity, authorization, and audit foundation is stable.

---

## 6. Technology Stack

### Backend

* .NET 10
* ASP.NET Core Web API

### Primary Database

* PostgreSQL

### ORM

* Entity Framework Core

### Cache and Temporary State

* Redis

### Search and Reporting

* Elasticsearch

Elasticsearch is deferred until a real reporting or search use case requires it.

### Authentication

* JWT access tokens
* Rotating refresh tokens
* Refresh-token reuse detection

### API Style

* REST
* OpenAPI / Swagger
* Explicit request and response contracts

### Local Infrastructure

**Resolved (PLAN-001):** For Sprint 1, Docker Compose contains PostgreSQL only.
Redis and Elasticsearch are not added until a current task requires them.
Integration tests use a real PostgreSQL instance through Testcontainers; the EF
Core InMemory and SQLite providers are not substitutes for RLS and
PostgreSQL-specific tests. Development secrets and connection strings must not be
committed.

---

## 7. Architecture Style

The system will use a modular monolith.

Reasons:

* Financial operations require strong transactional consistency.
* A single deployment is easier to develop and operate initially.
* The project does not currently justify distributed-system complexity.
* A modular structure supports clear ownership and future extraction.
* PostgreSQL transactions can safely cover early cross-module business operations.

Microservices must not be introduced without a demonstrated requirement involving:

* Independent deployment
* Independent scaling
* Team ownership
* Reliability isolation
* Technology separation
* Operational necessity

### [RESOLVED] Module Boundary Enforcement

**Resolved (PLAN-001, ADR-004, ADR-011):** One project per business module, each
with a small `.Contracts` project holding its public surface; internal `Domain`,
`Application`, and `Infrastructure` folders inside each module project; one
API/Host project; one small `BuildingBlocks` project. Initial modules only:
Identity, Organizations, Authorization, Audit. Implementation types are
`internal`; other modules reference only the owning module's `.Contracts`
project. Enforcement uses one DbContext and one PostgreSQL schema per module,
independent migrations, and architecture tests. Cross-module communication uses
synchronous public contracts and synchronous in-process integration events, with
audited administrative operations committing atomically via an explicit
transaction coordinator that shares one Npgsql connection and transaction.

### [RESOLVED] Frontend Repository and Browser Boundary

**Resolved (IMPL-017, ADR-037):** This repository owns the backend modular
monolith. The finance/customer portal and recipient gift-card application are
independent repositories and consume the versioned OpenAPI contract. Production
browser traffic uses a same-origin BFF/reverse proxy that keeps rotating refresh
tokens server-side and supplies CSRF protection. The API remains bearer-only and
does not enable broad CORS. Frontend state and action visibility never replace
server-side permission evaluation, active-membership verification, PostgreSQL
RLS, or Ledger authority.

Regardless of the selected structure:

* A module must not directly modify another module's internal entities.
* A module must not use another module's internal DbContext.
* Cross-module access must use explicit public contracts.
* Domain code must not depend on ASP.NET Core controllers.
* Domain code must not depend on Redis or Elasticsearch.
* EF Core entities must not be exposed directly through API contracts.

The accepted decision must be recorded in `docs/DECISIONS.md`.

---

## 8. Intended Business Modules

### Foundation Modules

* Identity
* Organizations
* Authorization
* Audit

### Financial Modules

* Ledger
* Corporate Credits
* Gift Cards
* Distribution

### Sharing and Payment Modules

* Sharing
* Payments
* Redemption

### Supporting Modules

* Reporting
* Notifications

Supporting and future modules must not be implemented until they enter the
current task scope.

---

## 9. Multi-Tenancy and Isolation

Each corporate organization is a tenant.

No tenant may access another unrelated tenant's:

* Organizations
* Subsidiaries
* Users
* Memberships
* Roles
* Permissions
* Gift cards
* Corporate credit
* Ledger entries
* Transactions
* Reports
* Audit data

Every tenant-owned record must contain or be resolvable to the owning
organization.

Every tenant-scoped operation must derive organization scope from trusted
server-side authentication and membership context.

A client-provided `OrganizationId`, `TenantId`, or `MembershipId` is not proof
of authorization.

### [RESOLVED] Tenant-Isolation Mechanism

**Resolved (PLAN-001, ADR-005, ADR-019, ADR-020):** One shared PostgreSQL
database with a separate schema per module, `organization_id` on every
tenant-owned table, EF Core query filters for ergonomics and defense in depth,
and PostgreSQL Row-Level Security as the authoritative database-level isolation
barrier. Application-level filtering alone is not sufficient.

Each entity is classified as global, platform-scoped, or tenant-scoped; only
tenant-scoped entities carry `organization_id` and are subject to RLS. The
runtime application role is non-superuser, lacks `BYPASSRLS`, and is subject to
RLS; platform cross-tenant access uses a controlled execution context and RLS
policy path rather than a superuser connection or disabled RLS. RLS session
context is set per transaction via `SET LOCAL` (or an equivalent safe Npgsql
strategy) before any tenant-scoped SQL executes, protecting reads and writes and
remaining safe under connection pooling. Migrations run as a separate migration
owner role; background jobs and tests set the execution context explicitly;
integration tests prove pooled connections cannot reuse stale tenant context.

---

## 10. Organization Hierarchy

The platform operator owns the deployment and sits above every customer
organization.

Customer organizations may have subsidiaries.

Example:

```text
Platform Operator
└── Customer Holding
    ├── Customer Retail
    ├── Customer Logistics
    └── Customer Technology
```

Organization hierarchy does not automatically grant unrestricted access.

A parent organization user must have explicit permission scope to access or
manage subsidiaries.

### [RESOLVED] Maximum Hierarchy Depth

**Resolved (PLAN-001, ADR-010, ADR-021):** The platform operator is a distinct platform scope,
not a normal customer organization row, and is not counted in the depth limit.
The maximum initial customer-organization hierarchy depth is 5 levels,
configurable but enforced server-side at subsidiary creation. Cycles are
forbidden. Hierarchy is stored as `parent_organization_id` plus a materialized
`ltree` path (with a stored depth value where useful); reparenting updates
descendant paths atomically. If `ltree` is unavailable in the target
environment, the recorded fallback is adjacency list plus recursive CTE, chosen
deliberately rather than silently.

---

## 11. User and Organization Membership Model

A user account is global.

A user can belong to one or more organizations through organization
memberships.

A user may have different roles in different organizations.

Conceptual relationship:

```text
User
└── OrganizationMembership
    ├── Organization
    └── MembershipRoles
```

Authentication identifies the global user.

Organization-specific authorization evaluates the active membership.

A disabled organization membership must prevent access to that organization
without necessarily disabling the global user account.

A globally disabled user must lose access through all memberships.

Historical financial and audit records must remain attributable to the original
user and membership.

platform identities and organization staff created through controlled
administrative workflows remain email-only. A gift-card recipient instead owns
exactly one normalized login contact: either a globally unique email address or
a globally unique E.164 phone number. Claiming a card creates no organization
membership.

---

## 12. Authorization Model

The platform uses organization-scoped role-based access control with named
permissions.

Authorization must not rely only on broad role-name comparisons such as:

```csharp
if (user.Role == "Admin")
```

Example permissions:

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

credit.view
credit.allocate

giftcard.view
giftcard.create
giftcard.distribute
giftcard.cancel

audit.view
```

Organization-specific roles belong to one organization.

A role belonging to one organization cannot be assigned to a membership in
another organization.

A user must not grant permissions they do not possess unless they are acting
through an explicitly authorized platform capability.

### [RESOLVED] Hierarchy-Aware Authorization Scope

**Resolved (PLAN-001, ADR-006, ADR-021):** Scope is stored on the membership-role
assignment, not on the role, so a role is reusable with different scopes.

* `MembershipRoleAssignment`: `MembershipId`, `RoleId`, `ScopeType`,
  `AnchorOrganizationId` (when applicable)
* `MembershipRoleAssignmentScopes`: `MembershipRoleAssignmentId`,
  `OrganizationId`

Supported scope types are `Organization` (anchor only), `Subtree` (anchor and
all descendants via the ltree path), and `SelectedOrganizations` (one or more
explicitly granted organizations held in the separate relation above — never a
single optional identifier).

Platform-global authorization uses a separate platform-role assignment model for
platform operators and must not be represented as an organization-membership role
assignment. Only authorized platform operators may hold platform-global
permissions, and being a platform operator does not automatically imply every
platform permission.

Effective authorization evaluates the authenticated global user, the active
organization membership, assigned organization roles, the assignment scope, the
target organization, and the organization hierarchy path. Parent-organization
ownership alone never grants access.

---

## 13. Authentication and Sessions

The platform will use:

* JWT access tokens
* Rotating refresh tokens
* Refresh-token families
* Refresh-token reuse detection
* Session revocation

On every successful refresh:

1. The current refresh token is invalidated.
2. A replacement token is generated.
3. The replacement remains in the same token family.

If an already invalidated token is reused:

1. Treat the event as a possible compromise.
2. Revoke the relevant token family or session.
3. Require the user to authenticate again.

Persisted refresh tokens should be hashed when practical.

Disabling a global user must revoke their active sessions.

Disabling an organization membership must immediately prevent access to that
organization.

Authentication endpoints must be rate-limited.

Passwords must use established secure password-hashing facilities.

Custom password cryptography is forbidden.

Staff sign in with email. A recipient signs in with the email or phone number
verified during activation. Activation sends one message through the selected
channel; later password login, 15-minute JWT access, and rotating 30-day refresh
sessions send no email or SMS.

---

## 14. Audit Requirements

Administrative and security-sensitive actions must create audit records.

Examples:

* Organization creation
* Subsidiary creation
* User creation
* User disabling
* Membership creation or disabling
* Role creation
* Permission assignment
* Corporate credit allocation
* Gift card creation
* Gift card distribution
* Share-link creation
* Redemption
* Refund
* Session compromise detection

Audit records must be append-only.

The application database role must not have UPDATE or DELETE permissions on
committed audit records.

Audit records should identify, when applicable:

* Acting user
* Active membership
* Organization scope
* Operation
* Affected entity
* Timestamp
* Outcome
* Correlation identifier

Audit records must not contain:

* Passwords
* Password hashes
* Access tokens
* Refresh tokens
* Share tokens
* PIN values
* Pattern values
* Other reusable credentials

### [OPEN DECISION] Tamper Evidence

Possible later hardening approaches include:

* Database append-only privileges only
* Hash chaining
* Periodic signed checkpoints
* External immutable storage

This does not block basic Sprint 1 audit implementation but must be resolved
before production hardening.

---

## 15. Financial Model: Ledger First

All value-changing operations must be represented through immutable,
append-only ledger records.

Examples:

* Corporate credit allocation
* Gift card issuance
* Gift card distribution
* Balance transfer
* Balance sharing
* POS redemption
* Refund
* Expiration
* Cancellation
* Reversal

A mutable balance column must never be the sole financial source of truth.

A current balance may be:

* Calculated from ledger entries
* Materialized for read performance
* Cached as derived state

However, it must remain reconcilable against the ledger.

The Ledger module must be introduced together with the first corporate-credit
and gift-card financial use cases.

It must not be retrofitted after implementing mutable balance fields.

Ledger requirements:

* Entries are immutable after commit.
* Corrections use compensating entries.
* Every transaction has a unique business or idempotency identity.
* Financial and related domain changes commit atomically.
* Concurrent operations must not allow overspending.
* Monetary values use `decimal`.
* Currency is explicit.
* Financial timestamps are server-controlled or server-validated.
* Redis and Elasticsearch are never the financial source of truth.

### [RESOLVED] Ledger Representation

ADR-014 selects a posted-only balanced double-entry product ledger. Accounts
hold one currency; immutable transactions contain positive debit and credit
entries that balance per currency. Current balances are derived as credits minus
debits, corrections use compensating transactions, and operation-scoped
idempotency keys prevent duplicate financial effects. Pending/reserved entries
remain deferred until a concrete flow requires them.

---

## 16. Corporate Credit

The platform operator may allocate corporate gift card value to a customer organization.

Every allocation must identify:

* Source
* Recipient organization
* Amount
* Currency
* Initiating actor
* Commercial or business reference
* Timestamp
* Idempotency identity

Corporate credit must not be created by directly changing a mutable balance.

Every allocation must produce ledger records.

Retrying the same allocation must not duplicate value.

An organization must not distribute more value than it owns.

Reversals must use compensating ledger operations.

---

## 17. Gift Card Management

The platform will eventually support:

* Gift card creation
* Activation
* Suspension
* Expiration
* Cancellation
* Distribution
* Bulk distribution
* Transaction history

A gift card must have:

* Funding root organization
* Operational issuing organization
* Explicit ownership or ownership state
* Explicit currency
* Lifecycle status
* Validity rules
* Transferability policy
* Divisibility policy

Issuance moves root-owned corporate credit into organization inventory.
Distribution changes ownership without a ledger posting when no value moves.
`valid_from` defaults to server issuance time, `expires_at` is required, and
transferability/divisibility default to false.

The delivered issuance model starts a root card as lifecycle `Active` and
ownership `OrganizationInventory`, owned by the operational issuing
organization. It creates one single-currency `GiftCardValue` ledger account and
atomically debits the funding root's corporate-credit account. The card stores
the issuance amount as provenance but no mutable authoritative balance.

Root cards retain their own root lineage ID, a null source, and generation zero
so Phase 3 can create traceable descendants. Gift-card RLS admits the verified
funding tenant or exact identity owner; organization APIs independently require
named permissions. The `GC-` public reference is display/support data and is
never a payment credential.

Distribution may target a normalized email address or phone number when no user
exists. Claim uses an expiring, hashed, single-use token to create or associate
the recipient identity. Card receipt never grants organization membership.
Distribution changes ownership to `AwaitingClaim`; claim changes it to
`IdentityOwned` while leaving the dedicated card ledger account and entries
unchanged. Invitation identity is immutable, event history is append-only, and
claim is rate/attempt limited and concurrency-safe.

Cancellation and expiration return remaining value to the funding root through
compensating ledger entries. A company may cancel only before recipient claim;
after activation, only an explicitly authorized platform operator may perform an
emergency cancellation. Cardholders may suspend/reactivate only their own
cards. Suspension preserves ownership and value, including pausing an
awaiting-claim activation, while expiration is effective at server time and is
financially finalized by an idempotent PostgreSQL-coordinated process.

A gift card may not be redeemed when:

* Pending
* Suspended
* Expired
* Cancelled
* Fully consumed
* Otherwise inactive

Gift card identifiers shown to users must not act as permanent payment
credentials.

Gift card balances must remain derived from or reconcilable with the ledger.

Phase 2 exposes those guarantees through read-only PostgreSQL reporting rather
than a second financial store. Authorized customer and platform callers can read
per-currency summaries, stable cross-operation history, and deterministic
domain-to-Ledger reconciliation findings. An exact card owner can read their
cards, Ledger-derived balance, detail, and complete history without company
membership. Audit investigation has separate organization/platform
permissions. None of these reads repairs history or hides it merely because an
organization is suspended or a card is inactive.

---

## 18. Gift Card Sharing

An end user can share part of their controlled gift card value. IMPL-022
delivers protected links between two existing authenticated identities;
IMPL-023 delivers verified email/phone invitation for both new and existing
recipient identities through the same child-card and Ledger outcome.

Supported scenarios may include:

* Transfer to an existing platform user
* Invitation by email
* Invitation by phone
* Secure share link
* New recipient account creation

Sharing must not create new value.

A partial transfer must debit the source and credit the recipient through an
atomic ledger operation.

A share operation must be idempotent.

A secure share link must:

* Expire
* Be single-use
* Use a securely generated token
* Store the token as a hash when direct recovery is unnecessary
* Support cancellation before claim when policy allows
* Prevent concurrent duplicate claim

### Link Protection

Potential protection methods:

* PIN
* Password
* Pattern lock
* Email OTP
* SMS OTP

PINs, passwords, and pattern values must never be stored in plaintext.

### Accepted Reservation and Protection Policy

ADR-015 and ADR-016 require reservation at creation and Ledger transfer only at
successful claim. Owned-card reads distinguish posted, reserved, and available
value. Cancellation, expiry, permanent lock, and terminal source lifecycle
release the reservation without posting.

Every generic link uses a 256-bit opaque secret plus a six-digit PIN, expires
after exactly 24 hours, is single-use, and locks permanently on the fifth wrong
PIN attempt. Only token and PIN hashes persist. Raw credentials are returned
once with no-store semantics, never logged or audited, and GET/link-preview
traffic never claims. Generic claim requires a different authenticated existing
recipient and atomically creates a separate child card/account with immutable
source/root/generation lineage and one balanced Ledger transfer.

Password and pattern protection are deferred. Direct contact-bound sharing
must reuse the existing Distribution/Identity activation boundary rather than
create another identity authority.

---

## 19. Dynamic QR and Payment Code

The client will eventually generate a short-lived payment credential.

The credential may be shown as:

* QR code
* Numeric code

The code is expected to refresh approximately every 60 seconds.

The credential must:

* Be short-lived
* Be single-use
* Avoid exposing authoritative balance information
* Prevent replay
* Be safe under concurrent scans
* Be bound to a valid gift card or spending context

### [RESOLVED] QR Token Design

**Resolved (ADR-017):** The credential is a 256-bit opaque CSPRNG value, not a
JWT. It encodes no card, owner, amount, or balance; the backend resolves it by
server-side lookup to the card, its exact current owner, and the Ledger-derived
balance. Token state lives in PostgreSQL beside the card, provision, and Ledger
rather than in Redis, so single-use consumption is enforced by the same
transactional guarantees that protect money. The TTL is 60 seconds, evaluated
against the server clock only — no clock-skew allowance is needed because no
client asserts a time. A token is consumed exactly once under a row lock in the
transaction that creates the payment provision; consumed, expired, and unknown
tokens are refused identically. POS client and terminal authentication is
separate and additionally required (ADR-043).

---

## 20. POS Redemption

Payment begins with a first-class, time-bounded provision. Creating a provision
validates the card and reserves value, so active provisions reduce available
value without creating a posted ledger movement. Provision states are
`Active`, `Confirmed`, `Cancelled`, and `Expired`.

Confirmation consumes one active provision exactly once and atomically posts
the redemption ledger transaction. Cancellation and expiration release the
reservation without a ledger posting. Provision creation, status,
confirmation, cancellation, and expiry must be idempotent and concurrency-safe
against sharing and other payment attempts (ADR-033).

Confirmation charges an explicitly stated amount, at or below the provisioned
ceiling, and releases any remainder in the same transaction; a larger charge
requires a new provision and therefore a new credential (ADR-046). The posting
debits the gift-card value account and credits a platform-scoped per-currency
redemption settlement account, whose balance must always equal posted
redemptions less posted refunds (ADR-045). A retry presenting the same
credential returns the original outcome with no time limit.

The POS will submit:

* Payment token or code
* Purchase amount
* Store identity
* Terminal identity
* POS transaction reference

The platform must validate:

* POS identity
* Token validity
* Token expiration
* Token single-use status
* Gift card status
* Gift card validity period
* Available spendable value
* Any applicable store or policy restrictions

Validation and value deduction must form one concurrency-safe business
operation.

The system must prevent two simultaneous redemptions from spending the same
available value.

Every successful redemption must create immutable ledger records.

Redemption failures must not expose unnecessary gift card, balance, or user
information.

---

## 21. Idempotency

Every financial operation must be idempotent.

Examples:

* Corporate credit allocation
* Gift card issuance
* Gift card distribution
* Sharing
* Claim
* Redemption
* Refund
* Reversal

A retried request must return the original outcome instead of creating a second
financial effect.

For redemption, idempotency is derived solely from the server-issued credential
identity. POS transaction, terminal, and store references are attribution and
reconciliation data, not caller-chosen safety identity (ADR-018).

A freely selected client-only idempotency key must not be the sole
double-spend protection.

ADR-018 fixes that key composition before redemption implementation.

---

## 22. Redis Responsibilities

Redis may later be used for:

* Temporary QR token state
* Rate limiting
* Short-lived verification state
* Permission caching
* Session-related cache
* Temporary lockout state
* Distributed coordination where justified

Redis must not be used as the only source of truth for:

* Organization ownership
* Membership
* Roles
* Permissions
* Corporate credit
* Gift card balances
* Ledger entries
* Final redemption outcomes

Redis must not be added during the foundation phase without a current use case.

---

## 23. Elasticsearch Responsibilities

Elasticsearch may later be used for:

* Operational search
* Audit-log querying
* Gift card transaction search
* Corporate reports
* Analytics dashboards
* Read-oriented projections

Elasticsearch is not authoritative.

Indexed data must be tenant-scoped.

Search queries must not permit cross-tenant leakage.

Elasticsearch must not be introduced until an accepted task requires it.

---

## 24. Core Functional Roadmap

### Phase 1 — Foundation

* Identity and authentication
* JWT access tokens
* Rotating refresh tokens
* Organization management
* Subsidiary hierarchy
* User management
* Organization memberships
* Roles
* Permissions
* Hierarchy-aware authorization
* Append-only audit
* Tenant-isolation integration tests

### Phase 2 — Money

* Ledger
* Corporate credit allocation
* Company value accounts
* Gift card creation
* Gift card lifecycle
* Root-funded organization inventory with separate issuing-organization scope
* Accountless email/phone distribution, claim, and activation
* Bounded all-or-nothing bulk distribution
* Financial history
* Organization financial summaries and read-only reconciliation
* Recipient My Cards, Ledger-derived balances, and complete history
* Permission-protected tenant audit investigation
* Concurrency and idempotency tests

The implementation sequence and its exit evidence are recorded in the commit
history and in `CHANGELOG.md`.

### Phase 3 — Sharing

* Partial balance transfer
* Share invitation reusing the Phase 2 accountless claim mechanism
* Separate recipient card and source/child lineage
* Immediate, authoritative reservation at share creation
* Ledger transfer only on successful claim
* Secure generic share links protected by a six-digit PIN
* 24-hour link expiration and permanent lock after five failed attempts
* Single-use claim and sender cancellation before claim
* Atomic claim
* Concurrent-claim protection

The accepted delivery sequence and exact Phase 3 exit evidence are maintained
in `PHASE_3_PLAN.md`. Generic links are claimed by authenticated existing
recipients. New-recipient creation uses a contact-bound email/phone invitation
so identity verification is not replaced by possession of a link and PIN.

Backend delivery is implemented through IMPL-024. Owner history is filter-bound
and RLS-enriched with visible public card references; staff financial history
contains masked Sharing events; and read-only reconciliation checks active
reservations, claim transfers, and immutable child lineage. Shared child cards
are not counted as new corporate-funded issuance. Client pinning and the
synchronized three-repository Phase 3 release candidate remain phase-exit work.

### Phase 4 — QR and POS

* Dynamic QR
* Numeric payment code
* POS authentication
* Payment provision create/status/confirm/cancel/expire
* Available versus reserved value
* Redemption confirmation
* Token replay prevention
* Concurrent-spend protection
* Refund and reversal
* Store, terminal, receipt, payment, refund, and reversal reporting
* Elasticsearch integration where justified

Backend delivery is implemented through IMPL-031. PostgreSQL now supplies
permission-protected store, terminal, receipt, payment, refund, and derived
full-reversal reporting over the immutable Phase 4 records; Elasticsearch was
not justified by a measured scale or latency requirement. The separately
deployed POS counter application remains future work, and synchronized Phase 4
client convergence/release remains phase-exit work.

---

## 25. Initial Database Scope

### Phase 1

Tables are grouped by owning module schema (ADR-004) and tenancy category
(ADR-005).

`identity` schema:

* Users — global, exactly one email or E.164 phone login contact
* Sessions — global
* RefreshTokens — global

`organizations` schema:

* Organizations — tenant-scoped (`parent_organization_id`, ltree path, depth)
* OrganizationMemberships — tenant-scoped

`authorization` schema:

* Permissions — global definitions
* Roles — tenant-scoped (organization-specific roles)
* RolePermissions — tenant-scoped
* MembershipRoleAssignments — tenant-scoped (`ScopeType`, `AnchorOrganizationId`)
* MembershipRoleAssignmentScopes — tenant-scoped (selected-organization list)
* Platform-role assignments for platform operators — platform-scoped (ADR-021)

`audit` schema:

* AuditLogs — tenant-scoped where applicable, append-only (ADR-008, ADR-019)

Exact table names are finalized during scaffolding.

### Phase 2

* FinancialTransactions
* LedgerEntries
* Wallets or financial accounts
* CorporateCreditAllocations
* GiftCards
* GiftCardDistributionInvitations
* GiftCardDistributionEvents
* Notification delivery records or a rebuildable delivery projection

### Phase 3

* ShareLinks
* ShareClaims
* ActiveShareReservations
* Source/child gift-card lineage
* ShareProtectionAttempts

Sharing owns reservation and claim lifecycle state in PostgreSQL. Completed
value movement remains a balanced immutable Ledger transaction; no separate
mutable balance-transfer authority is introduced.

### Phase 4

* PaymentTokens or Redis-backed ephemeral token state
* PaymentProvisions
* RedemptionTransactions
* RefundTransactions
* POSClients
* POSTerminals

---

## 26. Initial Validation Scenario

The foundation is successful when this scenario works:

1. A platform administrator signs in.
2. The administrator creates a customer organization.
3. The administrator creates or assigns the initial Company Administrator.
4. The Company Administrator signs in.
5. The Company Administrator creates a subsidiary.
6. The administrator creates an HR role.
7. The administrator grants the HR role only selected user-management permissions.
8. An HR membership is assigned the role.
9. The HR user creates an employee in the permitted organization scope.
10. The HR user cannot manage roles or organization settings.
11. An unrelated tenant cannot view or modify the organization.
12. A disabled membership cannot continue organization operations.
13. All relevant operations appear in append-only audit history.

---

## 27. Development Principles

* Implement the smallest current task.
* Do not implement future roadmap modules early.
* Prefer small, reviewable changes.
* Keep controllers and endpoints thin.
* Keep domain rules outside HTTP-specific code.
* Do not expose EF Core entities through the API.
* Use explicit request and response contracts.
* Propagate `CancellationToken`.
* Use asynchronous database operations.
* Enforce critical invariants through database constraints where appropriate.
* Do not introduce generic repositories without a demonstrated need.
* Do not introduce abstractions without a current use case.
* Do not introduce Redis or Elasticsearch because they merely appear in the technology stack.
* Add integration tests for tenant isolation and authorization boundaries.
* Record durable architectural decisions in `docs/DECISIONS.md`.

---

## 28. Reading Order for a Change

Before implementing anything:

1. This document, for scope and business purpose.
2. `docs/ARCHITECTURE.md`, for how the system is put together.
3. `docs/DOMAIN_RULES.md`, for the invariants the change must preserve.
4. `docs/DECISIONS.md`, for why the surrounding code is shaped the way it is.

Four properties are load-bearing, and a change that touches any of them needs a
decision recorded rather than an assumption made: tenant isolation,
authorization scope, financial consistency, and idempotency. The same applies
to database structure, organization hierarchy, payment tokens, and any path
that can reach across tenants.

---

## 29. Open Decisions Summary

### Resolved in PLAN-001

Recorded in `docs/DECISIONS.md`:

* Module project and assembly structure — ADR-004
* Module-boundary enforcement — ADR-004
* Cross-module communication and atomicity — ADR-011
* PostgreSQL tenant-isolation mechanism — ADR-005
* Tenant-context propagation — ADR-020
* Organization hierarchy depth and representation — ADR-010
* Hierarchy-aware authorization scope — ADR-006
* Internal and public identifier strategy — ADR-012
* PostgreSQL database roles — ADR-019
* Platform scope versus customer organization hierarchy — ADR-021
* Initial local infrastructure and test strategy — ADR-022
* Staff versus recipient identifiers and activation-only delivery — ADR-034

### Resolved for production hardening in PLAN-005

* Signed audit batch Merkle checkpoints and external immutable witness — ADR-013

### Resolved for Phase 4 in PLAN-004

* Dynamic QR token design — ADR-017
* Redemption idempotency-key composition — ADR-018
* POS client boundary — ADR-043

### Resolved during Phase 4 delivery

* Payment-provision reservation window — ADR-044
* Redemption counter-account and settlement representation — ADR-045
* Confirmation amount and terminal retry window — ADR-046

### Resolved for Phase 3 in PLAN-003

* Share reservation timing and release/claim behavior — ADR-015
* Generic share-link protection, TTL, attempt bound, and identity boundary —
  ADR-016

Only decisions required by the current implementation task should be resolved
immediately.

Decisions for later phases may remain open until their module becomes current.
