# Generalization and 1.0 Scope Audit

**Status:** Critical product and architecture review; not an accepted roadmap or
implementation plan  
**Reviewed:** 2026-09-07  
**Scope:** `open-giftcard`, `open-giftcard-portal`,
`open-giftcard-cardholder`, and `open-giftcard-pos`

## Purpose

This review asks whether Open Giftcard is ready to become a broadly reusable,
open-source gift-card foundation rather than a system optimized for its original
large-supermarket use case.

It evaluates:

- which existing capabilities are universal platform primitives;
- which capabilities are valuable but belong in optional profiles;
- which assumptions should stop being universal invariants;
- which missing abstractions would force adopters to fork the core;
- what must be resolved before the public `/api/v1` contract becomes stable;
- and what can safely remain later work.

The review used the published documentation, OpenAPI contract, release metadata,
and selected configuration or source locations where documentation alone could
not establish a boundary. It also stress-tested the system from three independent
adopter perspectives:

1. a multi-country food-delivery marketplace;
2. a white-label SaaS, rewards, and reseller platform;
3. a coalition of small businesses, nonprofits, campuses, events, and municipal
   voucher programs.

## Executive verdict

Open Giftcard has an unusually strong financial and security kernel. It is not
yet a general-purpose gift-card platform.

The current product is a sophisticated implementation of one closed-loop model:

> A platform operator allocates prepaid value to corporate customers; HR or
> Finance distributes cards to employees; recipients present short-lived codes
> at operator-controlled tills; and redemption accumulates in a platform
> settlement account.

That business model is explicit in [PROJECT_DEFINITION.md](PROJECT_DEFINITION.md)
and is well implemented. The problem is not weak engineering. The problem is
that several decisions belonging to this particular product have become
platform-wide domain invariants.

The current statement that the only meaningful missing work is deployment is
therefore true only for the original corporate-retail scope. It is not true for
broad open-source adoption. A marketplace, restaurant network, local business
alliance, campus, consumer gift-card issuer, or restricted-aid program would need
to modify core schemas and contracts rather than configure or extend them.

The present `/api/v1` contract should not become stable until those
contract-shaping assumptions are corrected. Freezing it now would freeze the
original customer's organization chart and checkout lane into every adopter's
architecture.

The recommended direction is not to delete the supermarket functionality. It
should remain the flagship `corporate-rewards + retail-pos` profile, built on a
more general gift-card program kernel.

## Current project state

### What is strong

The four repositories have a coherent `v0.9.1` release set, extensive automated
verification, explicit compatibility metadata, and a notably honest distinction
between source verification and production certification.

The backend correctly states that:

- all four functional phases are implemented;
- `v0.9.1` is still an unstable `0.x` release;
- nothing has been deployed to a named environment;
- the upgrade path has not yet exercised a later schema migration;
- and the project is not making a production warranty.

The strongest parts of the project are substantially more mature than a typical
open-source starter:

- forced PostgreSQL Row-Level Security;
- trusted execution-context propagation;
- balanced, posted-only, append-only double-entry accounting;
- idempotent financial operations;
- concurrency-safe reservations and spending;
- compensating reversals and refunds;
- append-only audit with optional tamper-evident sealing;
- secure claim, share, and payment credentials;
- transactional notification delivery;
- restart-safe asynchronous batches;
- authoritative reconciliation;
- secure browser BFF boundaries;
- pinned OpenAPI snapshots;
- release artifacts, checksums, SBOMs, and upgrade gates.

These are appropriate foundations for 1.0 and should not be weakened during
generalization.

### Where the recorded state is inconsistent

The documentation does not currently provide one reliable statement of product
and release truth:

- The main README calls `v0.9.1` the current release, while
  `RELEASE_READINESS.md` still describes a future `v0.5.0-rc.1` and historical
  missing tags.
- `VERSIONING.md` assigns future deployment evidence to `v0.5.0` even though
  `v0.9.1` already exists. A release numbered `0.5` cannot sensibly follow
  `0.9.1`; deployment certification should be evidence attached to a monotonic
  release or a separate certification status.
- Client production-readiness documents still say canonical tags do not exist.
- `POS-STRATEGY.md` remains in the recommended reading path while describing
  partial approval, balance inquiry, provision idempotency, and POS hardening as
  missing even though they were subsequently implemented.
- Portal documents variously describe English-first and Turkish-default behavior.
- The documentation index says internal task histories and handoff documents are
  absent, although they are present.
- The documentation formally says that when code and documentation disagree, the
  documentation is wrong. That is an honest maintainer rule but an unsuitable
  adopter contract for a project aspiring to be a reference standard.

Before 1.0, the public documentation should clearly separate:

- current product behavior;
- current capability and limitation status;
- stable architecture decisions;
- supported deployment profiles;
- integration guides;
- and archived plans, task histories, reviews, and superseded research.

## Reusable core worth preserving

### Financial kernel

Keep the following as non-negotiable core behavior:

- immutable balanced ledger transactions;
- single-currency ledger accounts;
- explicit currencies;
- decimal arithmetic;
- compensating corrections instead of rewritten history;
- ledger-derived or ledger-reconcilable balances;
- idempotent business operations;
- concurrency-safe reservations and spending;
- atomic domain, ledger, and audit effects;
- explicit authorization, capture, cancellation, and refund states;
- and read-only reconciliation that never silently repairs financial history.

### Security and tenancy kernel

Keep:

- tenant isolation enforced by the database;
- server-derived scope rather than caller-asserted tenant authority;
- human memberships and permission-based roles;
- assignment-level authorization scope;
- separate platform authority;
- credential hashing and one-time disclosure;
- rotating refresh sessions and reuse detection;
- same-origin browser BFFs;
- rate limiting and uniform credential refusals;
- append-only audit;
- and privacy-reduced operational views.

The actor and resource model must become more general, but the security posture
should remain.

### Operational kernel

Keep:

- transactional outbox semantics;
- resumable background processing;
- bounded retry and dead-letter behavior;
- stable external references and cursors;
- readiness that detects unapplied migrations;
- observable worker health;
- release artifact verification;
- and contract compatibility testing.

## Hyper-specific features and their disposition

| Existing feature or assumption | 1.0 disposition | Reason |
| --- | --- | --- |
| Corporate-credit allocation followed by employee distribution | Optional **Corporate Rewards** profile | Employer rewards are valuable but not the universal funding model. |
| Five-level holding/subsidiary hierarchy | Keep the generic hierarchy capability; make it optional in product profiles | The authorization primitive is reusable; the holding-company interpretation is not. |
| HR/Finance-oriented portal workflows | Optional Corporate Rewards UI | Small merchants, consumer issuers, and API-first adopters should not inherit these workflows. |
| Synchronous 1-100 and asynchronous 1-2,000 spreadsheet-style batches | Optional bulk-distribution/import module | Well implemented, but explicitly payroll-shaped. |
| Partial-balance sharing with child-card lineage | Optional Sharing module | Required by some consumer programs and forbidden or irrelevant in others. |
| Fixed share policy: 24 hours, six-digit PIN, five attempts | Default policy preset, not a universal invariant | Security limits should remain bounded but configurable by program and channel. |
| E-pin reseller with prepaid corporate float and orphan PIN claim | Optional reseller/distribution-channel module | It is a specialized sales channel rather than a basic gift-card primitive. |
| Sixty-second QR and 12-digit numeric code | Optional retail presentation adapter | The secure presentation mechanism is useful; payment core must remain channel-neutral. |
| Two-minute counter hold | Retail policy preset | Online, scheduled, delivery, and hospitality flows need different authorization lifetimes. |
| Platform-owned POS registry | Retire as a universal invariant | Merchants and tenants must be able to own and manage their acceptance credentials. |
| Store represented only by `storeReference` | Retire as the domain model | Preserve the external reference, but introduce first-class merchant and location entities. |
| One platform redemption-settlement account per currency | Keep only in a simple closed-loop profile | Multi-merchant systems need merchant clearing, payable, fee, and payout attribution. |
| Mandatory card expiration | Retire as a universal invariant | Programs and jurisdictions require different expiry behavior, including no expiry. |
| Expiry/cancellation always returns value to the corporate funding root | Replace with program policy | Liability and value disposition differ by program. |
| Post-claim emergency cancellation reserved to platform staff | Replace with program governance policy | Aid sponsors, issuers, merchants, and hosted operators need different authority models. |
| Exactly one globally unique email-or-phone/password recipient identity | Built-in identity profile | Hosted brands need separate identity realms, external subjects, or anonymous/custodial ownership. |
| English/Turkish compiled UI and Turkish portal default | Locale packs and reference defaults | Language, timezone, and legal copy must be deployment- or program-controlled. |
| Source-edited branding | Replace with runtime configuration before 1.0 | Requiring source edits contradicts the promise of adoption without a core fork. |
| Merkle checkpoint signing plus WORM custody | Optional high-assurance audit profile | Append-only audit belongs in core; specialized custody should not burden a small deployment. |
| Portal, cardholder, and POS lockstep versioning | Replace with independent client compatibility ranges | The backend kernel and optional reference clients should not require simultaneous releases forever. |

## Missing abstraction 1: gift-card programs

The current model primarily carries policy through per-card booleans and
platform-wide constants. It lacks a first-class gift-card program or product.

Every issued card should reference an immutable version of a `ProgramPolicy`.
That policy should define at least:

- program owner and issuer;
- liability owner and funding model;
- allowed currencies and denominations;
- minimum and maximum value;
- optional, required, or absent expiration;
- expiration and cancellation value disposition;
- reloadability;
- transferability and divisibility;
- sharing behavior;
- ownership strategy: identified, external-subject, custodial, or anonymous;
- accepted merchants, locations, channels, and geographies;
- eligible purchase components or purpose codes;
- per-transaction, daily, and velocity limits;
- refund destination and cancellation rules;
- credential and authorization lifetimes;
- branding and notification template selection;
- terms, privacy, and support references.

Issued cards must retain the policy version under which they were created. A
later program edit must not silently rewrite the behavior or legal terms of
existing instruments.

This should be a bounded declarative policy model, not an arbitrary scripting
engine. Advanced decisions can be delegated through a carefully designed policy
adapter once a real use case justifies it.

## Missing abstraction 2: participants and merchants

The current organization model effectively conflates several independent roles:

- tenant;
- corporate customer;
- sponsor or funder;
- issuer;
- distributor;
- merchant;
- platform operator;
- and settlement beneficiary.

The core needs explicit participant roles such as:

- `Operator`;
- `ProgramOwner`;
- `Issuer`;
- `Sponsor` or `Funder`;
- `Distributor`;
- `Merchant` or `Redeemer`;
- `AcceptanceLocation`;
- `Cardholder` or `Beneficiary`;
- `SettlementPayee`.

These roles may be held by the same organization in a simple supermarket
deployment, but the data model must not assume they are the same.

Merchant and location ownership must determine:

- machine and server credentials;
- acceptance permissions;
- transaction visibility;
- refund authority;
- settlement attribution;
- reporting scope;
- and operational quotas.

The current platform-global POS registry and unowned store string block every
multi-merchant adopter at this boundary.

## Missing abstraction 3: channel-neutral commerce

The secure payment credential and provision implementation is a strong retail
adapter, but it should not define the universal payment contract.

The core should expose a channel-neutral payment intent or authorization model
that can bind:

- program and instrument or wallet;
- merchant and acceptance location;
- order or external transaction reference;
- requested amount and currency;
- eligible amount components;
- intended payees or settlement allocations;
- authorization deadline;
- capture mode;
- caller idempotency identity;
- and channel-specific presentation context.

QR, numeric code, web checkout, mobile app, POS bridge, barcode, and future
wallet-pass presentations can then resolve into the same authorization core.

The core lifecycle should support:

- create;
- authorize or reserve;
- status and recovery after an ambiguous response;
- bounded increment or decrement;
- one or more captures where the program permits them;
- finalization;
- cancellation or expiry;
- partial and full refund;
- and configurable refund destinations.

The existing single-capture, two-minute profile should remain the default retail
path, not the only path.

## Missing abstraction 4: service integrations and events

Machine callers are currently modeled as special cases: POS clients, partner
clients, background workers, and human membership paths have separate authority
branches.

The platform needs one general service-principal model with:

- tenant, program, merchant, and resource scope;
- explicit grants;
- expiring and rotatable credentials;
- revocation;
- per-client quotas;
- audit attribution;
- allowed redirect or network constraints where applicable;
- and safe client-credential exchange.

The same evaluator should authorize merchant servers, issuers, distributors,
automation, POS devices, and background services without adding a new privileged
ledger branch for every integration type.

The existing transactional outbox should also expose durable integration events,
separate from recipient notifications. Required event families include:

- instrument issued, activated, suspended, expired, and cancelled;
- distribution accepted, delivered, failed, and claimed;
- payment authorized, adjusted, captured, cancelled, and expired;
- refund accepted and completed;
- settlement position or batch changed;
- and program policy version changed.

Webhook delivery needs signed payloads, event identifiers, retry, deduplication,
ordering metadata, replay, secret rotation, delivery history, and dead-letter
visibility.

## Missing abstraction 5: identity and data lifecycle

The built-in email-or-phone/password identity is a useful default but is not a
universal ownership model.

Before 1.0, the data model and contracts should permit:

- built-in account ownership;
- external trusted subject mapping;
- issuer-specific identity realms;
- anonymous or bearer instruments with strict limits;
- custodial or dependent ownership;
- multiple verified contacts;
- account linking and recovery;
- contact change;
- staff invitation and activation;
- and external identity providers.

If built-in authentication remains an official profile, password recovery,
verification, administrative recovery, and appropriate MFA must not remain
undefined.

The platform also needs explicit data-governance operations:

- data inventory and retention classes;
- user data export;
- contact correction;
- pseudonymization or anonymization that preserves ledger and audit evidence;
- consent or terms-version attribution where required;
- audited support access;
- and safe administrative exports.

## Missing abstraction 6: runtime product configuration

An open-source adopter should not need to fork the reference clients just to
change the product name.

Configuration should support deployment- and program-level:

- product and program names;
- logos and accessible alternative text;
- semantic color tokens;
- custom domains;
- email and SMS sender identities;
- localized notification templates;
- supported locale packs and default locale;
- timezone and clock preferences;
- terms, privacy, accessibility, and support links;
- enabled capabilities and navigation;
- and default program/profile selection.

Reference UI code may remain opinionated, but branding and ordinary feature
selection must not require source edits.

## Adopter stress tests

### Food-delivery marketplace

Consider a EUR 31 order containing EUR 26 of restaurant food, a EUR 3 delivery
fee, and a EUR 2 tip when the cardholder has EUR 25 available.

The current platform can partially approve a scalar amount, but it cannot state
which components are eligible, who receives each component, or whether the
program excludes tips. A scheduled order cannot use a 60-second credential and
two-minute hold. A restaurant substitution or later tip can require an amount
above the original hold, which the single-capture model rejects. Two restaurants
in one basket cannot be allocated and settled atomically. A central support
service cannot necessarily refund a merchant-originated transaction, and there
is no webhook contract for the order service.

A food-delivery adopter therefore needs to replace Payments, settlement,
identity, and merchant authorization rather than merely add an adapter.

### Local-business alliance

A city card accepted by 30 independent cafes needs:

- merchant-owned locations and credentials;
- acceptance rules;
- merchant-scoped reports;
- clearing and payable balances;
- commission or platform fees;
- settlement statements and exports;
- and adjustment/refund authority.

The current redemption is financially safe, but all value accumulates into one
platform settlement position and detailed payment reporting is platform-only.
The alliance cannot answer what it owes each cafe without rebuilding the core
financial model.

### Charity or municipal voucher

A restricted-aid program needs:

- allowed merchants or locations;
- purpose, category, or selected SKU restrictions;
- geographic and time-window restrictions;
- per-period spending limits;
- sponsor or caseworker governance;
- program-specific cancellation authority;
- beneficiary privacy;
- and defensible data retention and export.

The current documentation mentions applicable store or policy restrictions, but
there is no accepted policy or request model capable of evaluating them.

One existing decision should be preserved: a funder can see issuance and program
liability without automatically seeing a recipient's live balance and spending
history. That is an excellent privacy boundary for employees and beneficiaries.

### University or campus wallet

A campus needs recurring grants, reloads, multiple restricted purses, student
SSO, department or guardian funding, campus merchants, rollover, and offboarding.
The present corporate-credit-to-new-card flow and global built-in identity are
too narrow even though the ledger and tenancy foundations are suitable.

### Festival or event credits

Outdoor events commonly need anonymous or wristband ownership, kiosk loading,
merchant settlement, post-event refund or cash-out rules, and unreliable-network
operation. Offline authorization is appropriately high-risk and need not block
1.0, but its absence must remain explicit. Anonymous ownership, loading, and
merchant settlement are separate missing abstractions that should not be confused
with offline support.

### White-label SaaS

A hosted SaaS operator needs multiple issuers, brands, programs, domains, sender
identities, legal texts, locale defaults, and identity realms. Source-edited
branding and one global recipient namespace force a fork and create cross-brand
coupling.

### API-first rewards or benefits platform

An API-first adopter needs service accounts that can create or manage programs,
fund, issue, distribute, query, reconcile, and receive events without
impersonating a human. The current special partner and POS principals do not form
a general integration model.

## Required before the 1.0 contract freezes

These items are ranked as 1.0 requirements because they shape stable public
contracts and core ownership, RLS, ledger, or lifecycle models. Deferring them
would make later support technically additive but architecturally distorted.

### 1. Program and policy versioning

Introduce first-class programs and immutable policy versions. Move expiry,
sharing, reload, validity, limits, ownership, acceptance, refund, and value
disposition into those policies.

### 2. Participant, merchant, and acceptance topology

Separate operator, issuer, funder, distributor, merchant, payee, and cardholder.
Make merchant locations and merchant-owned machine/server credentials first-class.

### 3. Multi-party financial accounting

Add liability ownership, merchant clearing/payables, fees, adjustments, and
settlement attribution. Keep the present global settlement account as the simple
single-merchant profile.

### 4. Channel-neutral payment intent and lifecycle

Support online and delayed commerce without pretending to be a POS lane. Permit
program-controlled authorization duration, adjustment, capture, finalization,
cancellation, and refund behavior.

### 5. General value operations

Support issue, load or reload, scheduled grant, sponsor adjustment, and
configurable refund destination. Corporate-credit allocation should become one
funding adapter rather than the definition of funding.

### 6. General service principals and durable external events

Unify machine authority and publish a signed, retryable, replayable integration
event/webhook contract.

### 7. Identity and data-governance foundation

Permit external subjects and identity realms, complete the built-in account
lifecycle, and define export, correction, retention, and anonymization behavior.

### 8. Runtime branding, localization, and notification templates

Make ordinary adoption possible without editing client source.

### 9. API interoperability corrections

Before stability begins:

- publish exact decimal money semantics instead of advertising monetary values
  as OpenAPI `double`;
- resolve enum serialization differences rather than depending on narrow client
  converters;
- validate real serialized requests and responses against OpenAPI instead of
  maintaining handwritten route and field lists;
- ensure official clients tolerate every additive response change the 1.x policy
  permits;
- define stable resource version, idempotency, external reference, metadata, and
  event conventions;
- and publish conformance tests for custom clients.

### 10. Supported deployment profiles and onboarding

At minimum, publish supported profiles for:

- `minimal-single-merchant`;
- `corporate-rewards`;
- `retail-pos`;
- `reseller-epin`;
- `headless-api`;
- and `multi-merchant`.

A cafe should not need to understand subsidiary hierarchies, payroll batches,
partner minting, or WORM custody to start. The hardened topology should remain
available without being the only documented topology.

### 11. Release and documentation truth

Use monotonic versioning, publish one current capability matrix, archive stale
working records, run the existing named-environment and human-acceptance gates,
and complete at least one threat-model/security review before calling the public
contract a reference-standard 1.0.

## Valuable but not necessarily 1.0 blockers

These can follow additively if the underlying program, participant, identity,
funding, and transaction abstractions are correct:

- direct consumer-purchase payment-provider adapters;
- scheduled gifting, recipient messages, and campaigns;
- deterministic use of several eligible cards or balances;
- enterprise OIDC, SAML, and SCIM implementations;
- four-eyes approval for high-risk operations;
- large asynchronous exports and accounting connectors;
- advanced fraud scoring and review queues;
- category and SKU policy-provider integrations;
- merchant payout-provider adapters;
- native mobile applications;
- Apple or Google wallet passes;
- and push notifications.

## Later or explicitly outside core

These should not delay 1.0:

- foreign-exchange conversion inside a card;
- offline or store-and-forward authorization;
- NFC and wristband device implementations;
- full retail POS, catalogue, stock, tax, receipt, or cash-drawer systems;
- food ordering and courier logistics;
- loyalty earning and promotion engines;
- beneficiary case-management or student-information systems;
- provider-specific KYC, AML, payout rails, tax filing, or country reporting.

The core should expose integration seams for these domains without attempting to
become them.

## Proposed 1.0 acceptance tests

The generalization work should be judged through adopter outcomes rather than by
the number of optional features shipped.

1. **Single merchant:** A cafe can deploy, brand, fund, issue, accept, refund,
   and reconcile a one-merchant program without editing source.
2. **Food delivery:** An order service can authorize, later capture, cancel, and
   refund an online order without pretending to be a physical POS lane.
3. **Multi-merchant:** A business alliance can onboard merchants and reconcile
   what each is owed.
4. **Restricted value:** An aid program can limit where and for what purpose
   value may be spent.
5. **White-label:** One operator can host two brands with separate policies,
   configuration, and identity realms.
6. **Headless integration:** A service principal can issue, redeem, reconcile,
   and observe lifecycle events without a human session.
7. **Original supermarket:** The existing corporate allocation, employee bulk
   distribution, cardholder, QR/POS, refund, audit, and reconciliation journey
   remains available as a supported profile.

If any test requires modifying Ledger, Payments, Identity, RLS policies, or core
database tables in an adopter fork, the abstraction is not ready for 1.0.

## Questions to resolve about optional features

These questions should be answered before modules are physically separated or
the stable contract is designed.

### Optional means what?

- Can a module be disabled only in the UI, or is it absent from runtime,
  migrations, permissions, workers, and OpenAPI?
- Is optionality per deployment, tenant, program, or individual card?
- Can a capability be enabled after cards already exist?
- What happens when a capability is disabled while active shares, holds, batches,
  or invitations still exist?
- Does the core promise compatibility with third-party modules, or only with the
  official profiles?

### Corporate Rewards

- Is `CorporateCredit` retained as a public product term, or renamed to a generic
  funding-account operation with corporate allocation layered above it?
- Are organization hierarchies always installed but hidden, or truly optional?
- Should an organization run several programs with separate budgets and policy
  versions?
- Should bulk distribution reserve a whole batch budget or retain progressive
  per-row competition for funds?

### Sharing

- Is sharing enabled per program, card, or both?
- Are child cards the permanent representation for every transfer, or one
  implementation strategy?
- Which link lifetime, protection, recipient identity, cancellation, and maximum
  generation settings are configurable?
- Can a program prohibit transfers after partial use or across identity realms?

### Retail POS

- Is the POS registry owned by the operator, merchant, or either according to
  deployment profile?
- Is QR/numeric presentation merely one adapter over a payment intent?
- Which authorization lifetimes and capture modes may the retail preset select?
- Which party may refund, and where does value go if the original instrument is
  terminal?

### Reseller e-pin

- Is an e-pin a product type, a distribution channel, or an ownership/claim
  strategy?
- Can one distributor sell several issuers and programs?
- Can one program have several distributors with different limits and fees?
- Does prepaid float remain a profile rule rather than the only partner funding
  model?
- Which webhook and order-status contract replaces polling and bespoke support?

### Advanced audit custody

- Is signed checkpointing disabled, optional, recommended, or required by each
  supported profile?
- Does disabling it alter only deployment assurance, or public product claims?
- Can a small deployment start with append-only audit and later adopt external
  custody without rewriting history?

### Reference clients

- Do the portal, cardholder, and POS version independently against an API
  compatibility range?
- Which workflows are guaranteed product surfaces and which are examples?
- Are third-party frontends supported through a conformance kit?
- Does a headless backend release require simultaneous client releases?

## Final recommendation

Open Giftcard should keep its original supermarket solution, but name it as a
profile rather than allowing it to define the platform.

The stable core should describe:

- programs and immutable policies;
- participants and their roles;
- instruments, accounts, and ownership;
- funding and value movement;
- acceptance, authorization, capture, cancellation, and refund;
- merchant settlement attribution;
- identity and service principals;
- durable events;
- tenancy, authorization, audit, and reconciliation.

The supermarket profile should compose those primitives into corporate credit,
employee batches, sharing, QR presentation, and operator-controlled POS.

That distinction is the difference between an excellent bespoke system and an
open-source standard that other organizations can adopt without rewriting its
financial and security core.
