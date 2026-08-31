# Domain Rules

## Document Status

This document contains stable business, security, and consistency rules.

It must not contain temporary implementation plans or task-specific instructions.

When a durable rule changes, update this document and record the reason in
`docs/DECISIONS.md`.

---

## 1. Platform Terminology

### Platform Owner

The platform operator owns the deployment and is a distinct platform scope, not
a normal customer organization row.

Platform-level administrators may perform operations that customer-organization
users cannot perform. Platform staff use global user identities with separate
platform-role assignments and are outside the customer organization hierarchy.
Being a platform operator does not automatically imply every platform
permission; platform operations remain permission-based and audited.

### Organization

A corporate customer represented in the platform.

An organization may have subsidiary organizations.

### User

A global identity that can authenticate to the platform.

### Organization Membership

The relationship between a user and an organization.

Organization-specific roles and permissions are assigned through membership,
not directly to the global user account.

### Tenant Root

The root customer organization that owns one customer hierarchy and its
corporate-credit funding boundary.

### Issuing Organization

The organization or department whose authorized member issues or administers a
gift card. It may be a descendant of the tenant root.

### Corporate Credit

Gift card value purchased from or allocated by the platform operator to a
corporate customer.

### Gift Card

A digital instrument representing spendable value under defined ownership,
validity, transfer, and redemption rules.

### Ledger

The immutable financial record of all value-changing operations.

### Redemption

A POS-authorized operation that consumes gift card value.

---

## 2. Organization Rules

1. The platform operator may create customer organizations.
2. A customer organization may create subsidiaries only when the acting membership has the required permission.
3. An organization must not access an unrelated organization's data.
4. Organization hierarchy must not imply unrestricted access by itself.
5. Access to subsidiaries must be determined by explicit authorization scope.
6. Organization status must affect whether new operations are permitted.
7. Historical records must remain queryable after an organization is suspended or disabled.
8. Organization deletion must not destroy financial or audit history.
9. The maximum initial customer-organization hierarchy depth is 5 levels. The limit is configurable but must be enforced server-side at subsidiary creation.
10. Cyclic organization relationships are forbidden.
11. The platform operator is a distinct platform scope, not a normal row in the customer organization hierarchy. The platform scope is not counted in the depth limit.
12. Reparenting an organization must update all descendant hierarchy paths atomically.

---

## 3. User and Membership Rules

1. A user account and an organization membership have separate lifecycles.
2. A user may belong to more than one organization.
3. A user may have different roles in different organizations.
4. Authentication identifies the global user.
5. Organization authorization uses an active membership.
6. A disabled membership cannot perform organization-scoped operations.
7. Disabling one membership must not automatically disable unrelated memberships.
8. Disabling a global user account must revoke access across all memberships.
9. Historical actions must remain attributable to their original user and membership.
10. User deletion or anonymization must not break financial or audit references.
11. A client-supplied membership identifier must be verified against the authenticated user.
12. A user must not create or manage users outside their authorized organization scope.
13. Receiving or claiming a gift card does not automatically create an organization membership.
14. A recipient may claim a card without having an account before the invitation is sent.
15. Platform users and organization staff provisioned through
    administrative workflows use email identities.
16. A recipient identity has exactly one globally unique normalized login
    contact: either email or an E.164 phone number.
17. Claim reuses an existing active identity without changing its password; a
    new contact must satisfy the normal password policy.

---

## 4. Role and Permission Rules

1. Authorization uses named permissions.
2. Broad role-name checks must not be the primary authorization mechanism.
3. Organization-specific roles belong to one organization.
4. A role from one organization cannot be assigned to a membership in another organization.
5. A membership may have more than one role.
6. Effective permissions are derived from the active membership and its assigned roles.
7. A user must not grant a permission they do not possess unless operating through an explicitly authorized platform-level capability.
8. Parent-child organization relationships do not automatically grant all permissions.
9. Permission changes must invalidate any relevant authorization cache.
10. Authorization failures must not reveal protected data belonging to another tenant.
11. Sensitive authorization decisions must be enforced below the controller layer.
12. Platform permissions and customer-organization permissions must be distinguishable.
13. Authorization scope is stored on the membership-role assignment, not on the role, so a role is reusable with different scopes.
14. Supported assignment scope types are `Organization` (anchor only), `Subtree` (anchor and all descendants), and `SelectedOrganizations` (one or more explicitly granted organizations held in a separate relation, never a single optional identifier).
15. Effective authorization evaluates the active membership, its assigned roles, the assignment scope, the target organization, and the organization hierarchy path.
16. Platform-global authorization is a separate platform-role assignment model and must not be represented as an organization-membership role assignment.

---

## 5. Tenant Isolation Rules

1. Every tenant-owned record must be associated with or resolvable to an organization.
2. Every tenant-scoped query and command must enforce organization scope.
3. A client-provided `OrganizationId` is not proof of access.
4. Tenant scope must be derived from trusted authenticated context.
5. Tenant isolation must remain effective in background jobs and internal handlers.
6. Cross-tenant data access is denied by default.
7. Search indexes and caches must preserve tenant boundaries.
8. Cache keys for tenant-owned data must include the appropriate organization scope.
9. Logs and error messages must not expose another organization's protected data.
10. Tenant-isolation behavior requires dedicated integration tests.
11. Tenant isolation uses one shared PostgreSQL database with a schema per module, `organization_id` on tenant-owned tables, EF Core query filters, and PostgreSQL Row-Level Security as the authoritative database-level barrier. Application-level filtering alone is not sufficient.
12. Each entity must be classified as global, platform-scoped, or tenant-scoped. Only tenant-scoped entities carry `organization_id` and are subject to RLS.
13. The runtime application database role must be non-superuser, must not hold `BYPASSRLS`, must be subject to RLS, and must have only required privileges. Platform cross-tenant access uses a controlled execution context and RLS policy path, never a superuser connection or disabled RLS.
14. RLS session context must be established before any tenant-scoped SQL executes, must protect reads as well as writes, and must be safe under connection pooling without leaking one tenant's context into another request.
15. The active organization and tenant root are distinct trusted values. The active organization anchors membership and permission evaluation; the tenant root defines the customer data-isolation and corporate-funding boundary.
16. Tenant RLS admits rows from the caller's customer hierarchy but does not grant authority. Named permission scope against the exact target organization remains mandatory.

---

## 6. Audit Rules

1. Administrative and security-sensitive operations must create audit records.
2. Audit records are append-only.
3. Application behavior must not update or delete existing audit records.
4. Audit records must identify, where available:

   * Acting user
   * Active membership
   * Organization scope
   * Operation
   * Affected entity
   * Timestamp
   * Outcome
   * Correlation identifier
5. Audit records must not contain passwords, tokens, PINs, or other security credentials.
6. Disabling or deleting a user must not destroy historical audit attribution.
7. Failed sensitive operations may require audit records.
8. Database privileges for audit storage must prevent application-level update and delete operations.
9. Sealed audit history is committed in versioned, database-sequenced batches.
   Each checkpoint contains an RFC 9162-domain-separated SHA-256 Merkle root,
   chains to the previous manifest digest, and carries an ECDSA P-256 signature.
10. Elasticsearch may index audit data, but PostgreSQL remains authoritative.
11. Organization-member audit records must persist the active membership identifier.
12. Audit history is tenant-isolated by PostgreSQL RLS. A context-free runtime connection must not read audit rows.
13. The exact signed checkpoint manifest must be copied to an external immutable
    witness; database evidence alone cannot prove that a privileged database
    actor did not delete both records and their checkpoint.
14. Checkpoint signing and witnessing are asynchronous controls. Their outage
    must alert and delay sealing, but must not roll back or refuse an audited
    financial operation.
15. A checkpoint boundary may include only fully committed audit rows. Writers
    share a transaction advisory lock and the sealer takes the matching
    exclusive lock before selecting its bounded sequence range.

---

## 7. Corporate Credit Rules

1. Corporate credit is allocated through an explicit financial operation.
2. Every credit allocation must produce immutable ledger records.
3. Corporate credit must not be created by directly editing a mutable balance field.
4. Every allocation must identify:

   * Source
   * Recipient organization
   * Amount
   * Currency
   * Initiating actor
   * Business reference
   * Timestamp
   * Idempotency identity
5. Credit allocation must be atomic.
6. Retrying the same allocation must not duplicate value.
7. An organization cannot distribute more available value than it owns.
8. Financial history must remain available after organization suspension.
9. Reversals must be represented by compensating ledger operations instead of deleting history.
10. Corporate credit must be reconciliable against ledger entries.
11. Corporate credit is economically owned by the root customer organization.
12. A descendant may initiate an authorized issuance, but the funding root and operational issuing organization must be recorded separately.

---

## 8. Gift Card Rules

1. Gift card issuance must use available corporate value.
2. Issuance must create immutable ledger records.
3. A gift card must have an explicit owner or ownership state.
4. A gift card must have an explicit currency.
5. A gift card must have a lifecycle status.
6. A gift card may not be redeemed while suspended, expired, cancelled, or otherwise inactive.
7. Gift card status changes must be auditable.
8. Gift card expiration must not delete financial history.
9. Cancellation or refund must use compensating financial operations.
10. Transferability and divisibility must be explicit policy decisions.
11. Gift card identifiers exposed to users must not function as permanent payment credentials.
12. Current spendable value must be derived from or reconciliable against ledger entries.
13. Issuance funds a card in organization inventory; distribution changes ownership without a ledger posting when no value moves.
14. `valid_from` defaults to the server issuance time and `expires_at` is required.
15. Transferability and divisibility are explicit per-card policies and default to `false`.
16. Distribution may address a normalized email address or phone number even when no user exists yet.
17. Recipient activation uses an expiring, single-use, high-entropy invitation token whose persisted form is a hash.
18. Claim creates or associates the minimum verified global identity needed to own the card; it does not grant organization authority.
19. Cancellation and expiration return remaining value to the funding organization through compensating ledger entries.
20. The initial bulk issuance/distribution operation is synchronous, all-or-nothing, idempotent, and limited to 100 items.
21. An issued root card starts as lifecycle `Active` and ownership
    `OrganizationInventory`, owned by the permission-checked issuing
    organization.
22. Every issued card has one dedicated single-currency Ledger account; the
    card row does not own a mutable authoritative balance.
23. Issuance debits the funding root's corporate-credit account and credits the
    card account atomically with the card and audit record.
24. Root cards retain `root_gift_card_id = id`, no source, and generation zero;
    descendant cards must retain their source/root lineage.
25. Issuance idempotency is scoped to the funding tenant and changed intent
    conflicts.
26. Gift-card persistence must admit either the verified funding tenant or the
    exact identity owner through forced RLS, while APIs still require named
    permissions for organization operations.
27. Distribution changes the card to ownership/lifecycle `AwaitingClaim`; a
    successful claim changes it to ownership `IdentityOwned` and lifecycle
    `Active`.
28. Invitation identity, normalized contact, token hash, card, and tenant are
    immutable after creation. Distribution event history is append-only.
29. The initial claim-token defaults are 256 random bits, a 24-hour lifetime,
    and locking after five failed secret attempts; the HTTP claim endpoint is
    independently rate-limited by source.
30. Only the selected activation channel receives a notification. Password
    login, access-token use, and refresh rotation send no email or SMS.
31. Distribution and claim are idempotent, serialize card ownership, and must
    not add ledger entries when no value moves.
32. Company administrators with an explicit scoped permission may cancel a card
    only before recipient claim. After claim, only an explicitly authorized
    platform administrator may perform an emergency cancellation.
33. A cardholder may suspend and reactivate only a card they own; a cardholder
    cannot cancel it or return its value to the funding organization.
34. Suspension preserves ownership and ledger value. An awaiting-claim card
    returns to `AwaitingClaim` on reactivation; an organization-inventory or
    identity-owned card returns to `Active`.
35. Cancellation and expiration are terminal, close any pending activation
    path, and return the exact remaining ledger-derived value at most once.
36. Expiration is effective at server-evaluated `expires_at_utc` even before its
    PostgreSQL-coordinated financial finalizer runs.
37. Administrative lifecycle commands require a normalized reason and an
    idempotency key, and all lifecycle changes append immutable lifecycle and
    audit history.
38. A bulk batch requires both issue and distribute authority for the exact
    organization, contains 1–100 uniquely referenced items, and preserves
    request order in its durable result.
39. Every bulk item reuses the single-card issuance and distribution rules;
    child operation keys are deterministic from the batch and item identity.
40. A failed bulk batch leaves no batch, card, invitation, Ledger, audit, or
    notification effect. Activation delivery occurs only after the successful
    database commit.
41. Completed batch and item results are tenant-isolated and immutable. A
    matching retry returns them; a changed normalized intent conflicts.
42. Organization financial summaries and history are rooted at the economic
    tenant root and require both corporate-credit-view and gift-card-view
    authority; narrower subsidiary scope cannot widen into a root report.
43. Financial summaries and history are rebuilt from authoritative domain and
    Ledger records. Reporting may not copy them into a second authoritative
    history or mutate them.
44. Cross-operation history uses stable server-controlled occurrence time plus
    a deterministic event key for opaque cursor pagination without duplicates
    or gaps.
45. Reconciliation may identify missing, mismatched, unbalanced, out-of-range,
    or orphan records but must never repair, delete, or post financial effects.
46. A recipient may list, inspect, and read complete history only for cards
    whose current exact identity owner is that recipient. Company membership is
    not required and caller-supplied ownership is never trusted.
47. Ledger-derived owned-card balance and history remain visible to the exact
    owner when a card is suspended, cancelled, or expired; terminal state does
    not erase provenance.
48. Organization audit investigation requires the dedicated
    `organization.audit.view` permission; cross-tenant platform investigation
    requires `platform.audit.view`. Operation, outcome, correlation, and cursor
    inputs must be validated and parameterized.
49. Suspended organizations retain authorized financial and audit history.
    Suspension blocks ineligible writes but does not hide immutable evidence.
50. An e-pin reseller is anchored to one active root organization. Its live
    server-resolved partner principal, never request input or token tenant
    claims, determines the prepaid float and issuing tenant.
51. Partner minting requires an explicit client scope, a live reseller and
    client, a unique idempotency key, and an amount no greater than the
    configured per-e-pin/order cap.
52. Partner minting, corporate-credit debit, card issuance, orphan invitation,
    ownership transition, event, and audit record commit atomically. Failure
    leaves none of them behind.
53. An orphan e-pin invitation is not contact-bound. It requires both its
    high-entropy link secret and separate six-digit PIN, expires within the
    configured bounded lifetime, and shares the normal five-failure lock.
54. Persist only one-way/HMAC e-pin credential material. Raw link secrets and
    PINs may cross only the no-store reseller delivery response and the buyer's
    claim request; they must never enter logs or audit metadata.
55. An authenticated identity may attach a valid e-pin only to itself. An
    anonymous buyer may create a new identity with normalized email or E.164
    phone plus a password. If that identity already exists, the buyer must sign
    in first; e-pin possession must not authenticate an existing account.
56. Disabling a client or reseller stops new minting on the next request but
    does not invalidate already-sold e-pins. Unclaimed clawback is an explicit
    authorized lifecycle cancellation that closes the invitation and returns
    remaining value atomically.
57. The e-pin delivery derivation key is independent of JWT signing material,
    must be held in managed secret storage, and must remain stable while an
    idempotent mint retry may need to reproduce delivery credentials.

---

## 9. Sharing Rules

1. A user may share only value they are authorized to control.
2. Sharing must not create new value.
3. Creating a pending share reserves its exact amount immediately. Available
   value equals Ledger-derived posted balance minus active share reservations.
4. A partial share must debit the source and credit a separate child card
   through one atomic, balanced Ledger operation only when claim succeeds.
5. Cancellation, expiration, or terminal protection lock releases the
   reservation without posting a Ledger transaction.
6. Share creation, claim, cancellation, and expiration must be idempotent and
   concurrency-safe against each other and against other value-consuming work.
7. A secure generic share link must:

   * Expire after 24 hours
   * Be single-use
   * Require a six-digit PIN for every amount
   * Permit sender cancellation only before claim
   * Lock permanently after five failed PIN attempts
8. Persist only cryptographic hashes of the 256-bit link token and PIN. Raw
   generic-link credentials may be returned once at creation and must never be
   logged or returned again.
9. A GET, crawler, or link-preview request must never claim or consume a share.
10. Generic-link claim requires an authenticated existing recipient identity.
    New recipients use the contact-bound email/phone invitation mechanism from
    Phase 2; link-plus-PIN possession is not identity verification.
11. A successful claim creates a separately owned child card and preserves
    source card, root card, and generation lineage.
12. Multiple concurrent claims of the same share must not both succeed, and a
    concurrent spend must not consume reserved value.
13. Password and pattern link protection are not v1 behavior.

---

## 10. QR and Redemption Rules

1. Payment QR codes must contain short-lived payment credentials.
2. A QR payment credential must not expose authoritative balance data.
3. A QR credential must expire after a short configured period.
4. A QR credential must be single-use.
5. Two concurrent attempts using the same credential must not both succeed.
6. Validation and financial deduction must form one concurrency-safe business operation.
7. Redemption must produce immutable ledger records.
8. Redemption requests must be idempotent.
9. A retried request must return the original result instead of consuming value again.
10. POS clients must authenticate independently from end users.
11. QR and redemption endpoints require rate limiting.
12. QR validity is evaluated against the server clock only. No client asserts a
    time, so no clock-skew allowance applies (ADR-017).
13. The QR credential is a 256-bit opaque CSPRNG value resolved by server-side
    lookup, not a signed self-contained token. It encodes no card, owner,
    amount, or balance, its state lives in PostgreSQL, and its TTL is 60
    seconds (ADR-017).
14. Redemption failures must not leak unnecessary gift card or user information.
15. PostgreSQL remains authoritative for the final redemption outcome.
16. A POS payment begins with a time-bounded provision that reserves value before final confirmation.
17. Provision states are `Active`, `Confirmed`, `Cancelled`, and `Expired`.
18. Active provisions reduce available value but do not create posted ledger entries.
19. Confirming a provision exactly once creates the immutable redemption posting; cancellation or expiration releases the reservation without a value posting.
20. Provision creation, confirmation, cancellation, and expiration must be concurrency-safe against sharing and every other spending path.
21. A payment provision reserves value for 2 minutes, evaluated against the
    server clock (ADR-044). The window covers a normal counter sale without
    stranding a cardholder's value behind an abandoned till.
22. POS software authenticates as a registered POS client acting at a registered
    terminal. A QR credential identifies the spending context and POS
    credentials identify who is asking; neither alone authorises a payment
    (ADR-017, ADR-043).
23. Cross-tenant POS payment reporting is platform authority protected by
    `platform.payments.view`; POS principals and customer-organization users do
    not inherit it from payment participation or tenant finance access.
24. Payment reports are rebuildable PostgreSQL reads over authoritative
    provisions and immutable refunds. A derived store/terminal/receipt view,
    cursor, total, or full-reversal flag is never a competing financial state.
25. A full reversal is reported only when cumulative immutable refunds equal
    the confirmed amount. Reports must keep currencies separate and must not
    expose owner identity, payment credentials, POS secrets, or idempotency keys.

---

## 11. Ledger Rules

1. Every value-changing operation creates ledger entries.
2. Ledger entries are immutable.
3. Application code must not update or delete committed ledger entries.
4. Corrections use compensating entries.
5. Every financial transaction must have a unique business or idempotency identity.
6. Debit and credit effects must balance according to the selected ledger model.
7. Ledger writes and affected domain-state changes must commit atomically.
8. Balance projections may be cached or materialized.
9. A projection must be rebuildable or reconciliable from the ledger.
10. Financial operations must use `decimal`.
11. Currency must be explicit.
12. Concurrent operations must not permit overspending.
13. Financial timestamps must be generated or validated server-side.
14. Financial records must retain the initiating actor and source operation.
15. Redis and Elasticsearch must never replace the ledger as the source of truth.

---

## 12. Authentication and Session Rules

1. Passwords must use established secure password-hashing facilities.
2. Custom cryptographic password schemes are forbidden.
3. Refresh tokens must rotate after successful use.
4. Refresh-token reuse must be treated as a possible compromise.
5. Reuse detection must revoke the associated token family or session.
6. Persisted refresh tokens must not be stored in recoverable plaintext where hashing is practical.
7. Disabling a global user must revoke active sessions.
8. Disabling a membership must immediately prevent access to that organization.
9. Authentication endpoints require rate limiting.
10. Tokens and credentials must never be written to logs or audit payloads.
11. Staff authenticate by email. A recipient authenticates with the email or
    phone contact verified during claim plus the normal password.
12. Normal login and refresh must not depend on an email or SMS delivery.

---

## 13. Data and API Rules

1. API contracts must not expose EF Core entities directly.
2. External input must be validated.
3. Database constraints must enforce critical invariants where possible.
4. Public identifiers must not expose sensitive sequential information without an explicit reason.
5. API errors must not expose stack traces, credentials, or cross-tenant data.
6. Multi-record operations that form one business action must use a transaction.
7. Cancellation tokens must be propagated through asynchronous application and database operations.
8. Database migrations must remain focused on the task that introduced them.
9. Redis and Elasticsearch must not be added to a module without a current use case.
10. Read models must not be treated as authoritative write models.
11. Frontend repositories are API clients; they never own or duplicate
    identity, tenant, permission, ownership, or financial authority.
12. A current-user organization picker returns only the authenticated user's
    active memberships and does not grant authority. A selected organization
    must be re-verified through the normal authenticated
    `X-Organization-Id` request path.
13. Pre-selection membership/organization discovery is SELECT-only under RLS,
    exact-user, and unavailable after an organization context is selected.
14. Platform root-customer discovery requires the named
    `platform.organizations.view` permission and bounded server-side filtering.
15. Production browser clients use a same-origin BFF/reverse-proxy session
    boundary. Refresh tokens must not be stored in browser JavaScript storage,
    and broad cross-origin API access is not an accepted default.
16. OpenAPI is the frontend integration contract. UI visibility may improve
    usability but never replaces server-side authorization.

---

## 14. Deferred Decisions

The following are not accepted domain rules until formally decided:

* Audit hash-chaining strategy
* Real email and SMS provider selection

The following were resolved in PLAN-001 and are now accepted rules (see the
relevant sections above and `docs/DECISIONS.md`):

* PostgreSQL tenant-isolation mechanism (ADR-005, ADR-019, ADR-020)
* Module assembly layout and boundary enforcement (ADR-004)
* Cross-module communication and atomicity (ADR-011)
* Maximum organization hierarchy depth and representation (ADR-010)
* Hierarchy-aware authorization model (ADR-006)
* Platform scope versus customer hierarchy (ADR-021)
* Public identifier strategy (ADR-012)
* Ledger accounting representation (ADR-014)
* Public API versioning strategy (ADR-027)
* Phase 2 gift-card funding, delivery, lifecycle, and bulk defaults (ADR-030)
* Tenant-root versus active-organization semantics (ADR-031)
* Tenant-isolated audit and membership attribution (ADR-032)
* First-class payment provision lifecycle (ADR-033)
* Phase 2 read-only reporting and reconciliation boundary (ADR-036)
* Phase 3 share reservation and release timing (ADR-015)
* Phase 3 share-link protection and identity boundary (ADR-016)
* Phase 4 dynamic QR token design (ADR-017)
* Phase 4 redemption idempotency identity (ADR-018)
* Phase 4 POS client boundary (ADR-043)
* Phase 4 payment-provision reservation window (ADR-044)
* Phase 4 redemption counter-account (ADR-045)
* Phase 4 confirmation amount and retry window (ADR-046)

Two further Phase 4 rules follow from those decisions:

21. Redemption idempotency is derived from the server-issued QR token's
    identity, never from a client-chosen key. The POS transaction, store, and
    terminal references are recorded for reconciliation but are not what
    prevents a double charge (ADR-018).
22. POS software authenticates as a POS client against the versioned API and is
    never issued PostgreSQL credentials (ADR-043).
23. Redemption debits the gift-card value account and credits a platform-scoped
    per-currency redemption settlement account. Redeemed value never returns to
    the allocatable platform funding pool; refund and reversal debit settlement
    and credit the card (ADR-045).
24. Confirmation charges any positive amount up to and including the provisioned
    amount, and the request must state that amount explicitly. A larger amount is
    refused, so a bigger charge always requires a new provision and therefore a
    new credential. A partial confirmation releases its remainder in the same
    transaction; there is no partially-consumed provision state (ADR-046).
25. A confirmation retry presenting the same credential returns the original
    outcome with no time limit, because that credential maps to exactly one
    redemption permanently (ADR-018, ADR-046).
26. Every newly issued payment token has one opaque QR form and one CSPRNG
    12-digit numeric form. They share the same 60-second expiry and single-use
    consumption state; consuming either invalidates both (ADR-050).
27. Only hashes of payment credentials may persist. Numeric lookup is
    authenticated POS-only, rate-limited, exact-candidate scoped under RLS, and
    must return the same generic refusal for malformed, unknown, expired,
    consumed, or concurrently replayed input (ADR-050).
