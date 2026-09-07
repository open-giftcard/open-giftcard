# Generalization Roadmap to 1.0

**Status:** Product and architecture roadmap; milestone ordering is accepted as
direction, but individual domain changes still require focused ADRs  
**Created:** 2026-09-07  
**Scope:** Backend kernel, official modules, reference clients, adapters,
deployment tooling, documentation, and release process  
**Input:** [Generalization and 1.0 Scope Audit](GENERALIZATION_AND_1_0_SCOPE_AUDIT.md)

## Purpose

This roadmap turns the generalization audit into an ordered path to a maintainable
Open Giftcard 1.0.

The objective is not to accumulate features for every possible industry. The
objective is to establish the smallest stable set of abstractions that lets
different industries adopt the project without changing its financial,
authorization, tenancy, or audit core.

The product must remain:

- one maintained core release line;
- one financial and security model;
- one migration history;
- one stable API contract per major version;
- and one set of release-quality invariants.

There will be no independently forked Cafe, Supermarket, Campus, Charity, or
Food-Delivery editions. Those use cases will be expressed through versioned
program policies, participant roles, supported modules, deployment profiles, and
external adapters.

## 1.0 outcome

Open Giftcard 1.0 should be a secure, self-hostable platform for issuing,
holding, distributing, accepting, refunding, and reconciling gift-card or
restricted stored value across one or many participating organizations.

An adopter should be able to configure:

- who operates a deployment;
- who owns a program;
- who issues and funds value;
- who distributes it;
- who may hold it;
- which merchants and channels may accept it;
- which program policies apply;
- how value is authorized, captured, refunded, expired, or returned;
- which optional workflows are available;
- how the product is branded and localized;
- and which external providers deliver identity, notifications, loyalty,
  accounting, audit custody, or other integrations.

They must be able to do this without modifying Ledger, Payments, Identity, RLS
policies, or other core database internals.

## Non-negotiable invariants

Generalization must not weaken the qualities that make the existing project
valuable.

### Financial invariants

- Every value-changing operation produces immutable, balanced ledger entries.
- Corrections use compensating transactions; committed financial history is
  never rewritten.
- Balances remain derived from or reconcilable with the ledger.
- Financial operations remain atomic, idempotent, and concurrency-safe.
- Reservations cannot be double-spent across payment, sharing, or other value
  consumers.
- Currency is explicit and one ledger account holds one currency.
- Public money contracts use exact decimal semantics and defined currency
  exponents and rounding.
- A configurable feature may select a financial policy, but may not bypass
  ledger posting or reconciliation.

### Security invariants

- PostgreSQL RLS remains the authoritative tenant and participant isolation
  boundary.
- Client-supplied identifiers never prove authority.
- Human and machine actors receive explicit, least-privilege grants.
- Credentials remain hashed or encrypted as appropriate and raw secrets remain
  one-time disclosures.
- Browser clients keep backend tokens server-side.
- Sensitive operations are audited, including denied operations where required.
- Optional integrations fail closed when their result is required.
- Disabling a module, adapter, tenant, participant, or credential never deletes
  financial or audit history.
- No deployment profile relaxes the core financial or security model.

### Operational invariants

- Migrations are explicit, forward-only, and applied by a separate migrator.
- Readiness detects missing migrations and required unavailable dependencies.
- Background work is durable, restart-safe, bounded, and observable.
- External side effects use transactional outbox semantics.
- Upgrade and compatibility gates exercise real PostgreSQL.
- Every published artifact is versioned, checksummed, and attributable to exact
  source and contracts.

## Product composition model

Open Giftcard should use three extension layers with different trust levels.

### Core kernel and official transactional modules

The kernel and official modules that participate in financial transactions, RLS,
permissions, or migrations remain compiled, tested, and released together.

The kernel contains:

- programs and immutable policy versions;
- participants and resource ownership;
- identity and service principals;
- authorization;
- ledger and account roles;
- stored-value instruments and ownership;
- channel-neutral payment intents;
- authorization, capture, cancellation, and refund;
- merchant settlement attribution;
- audit;
- durable integration events;
- reporting and reconciliation contracts.

Official transactional modules may include:

- Corporate Rewards;
- Bulk Distribution;
- Sharing;
- Reseller E-pin;
- Retail POS acceptance;
- and advanced audit custody.

An official module may be enabled or disabled, but it is not downloaded as an
arbitrary in-process plugin. This keeps its migrations, ledger authority, RLS,
and upgrade behavior inside the tested release boundary.

### Deployment profiles

A profile is a versioned configuration preset over the same artifacts. It is not
a separate edition, fork, branch, or release line.

Supported profiles at 1.0 should be three:

- `corporate-rewards + retail-pos`, the existing system, which must keep working
  and is the regression baseline;
- `minimal-single-merchant`, the smallest useful deployment, which proves the
  kernel works without corporate credit or an operator-controlled till;
- `multi-merchant`, which proves participant topology and settlement
  attribution are real rather than modelled.

Those three are chosen because they are the ones that *prove the generalization*.
The first shows nothing regressed, the second shows the supermarket assumptions
are gone, and the third shows the new abstractions carry weight. A fourth adds
confidence but not evidence.

`community-alliance`, `reseller-epin`, `headless-api` and `hardened-enterprise`
move to post-1.0. This is a capacity judgement, not a design one. The
verification matrix below requires every supported profile to exercise the
financial, RLS, API, migration and recovery layers, so each profile is a
standing test burden and a standing support promise. Eight of those maintained
by one person means eight configurations that are each tested less well than
three would be.

A profile selects:

- enabled official modules;
- deployed services;
- safe initial program-policy defaults;
- visible portal workspaces;
- required infrastructure;
- recommended external adapters;
- and appropriate monitoring, backup, and assurance defaults.

After initialization, every selected setting becomes explicit configuration.
The profile name must not remain a hidden source of business behavior.

Optionality must be represented at three distinct levels:

| Level | Question | Authority |
| --- | --- | --- |
| Deployment capability | Is a module or adapter available in this deployment? | Deployment manifest |
| Program capability | May this program use sharing, reload, POS, or another behavior? | Versioned program policy |
| Instrument behavior | Under which policy version was this card issued? | Immutable instrument policy reference |

### External adapters

Third-party integrations should normally run out of process and communicate
through stable, language-neutral contracts.

Adapter categories include:

- email and SMS delivery;
- OIDC and external identity;
- external funding and payment providers;
- loyalty earning;
- accounting and settlement export;
- fraud or policy decisions;
- audit custody;
- and merchant payout systems.

Each adapter manifest must declare:

- adapter type and capabilities;
- compatible core contract range;
- configuration schema;
- secret fields;
- requested permissions and event subscriptions;
- health and readiness behavior;
- upgrade requirements;
- publisher and artifact signature;
- immutable container or package digest;
- and conformance-test status.

The core must not load arbitrary third-party assemblies into the financial
process. Extensions that need to change ledger semantics or trusted authorization
remain official reviewed modules.

## Release and repository model

### Version ownership

- The backend kernel and official transactional modules share one SemVer.
- Portal, cardholder, and POS reference clients version independently.
- Each client declares a tested compatible backend API range.
- External adapters declare compatible adapter-contract versions.
- A distribution manifest pins exact backend, client, adapter, profile, and
  container digests for a tested deployment set.
- Profiles themselves are versioned configuration schemas, not product releases.

### Publishing rules

- Use monotonic SemVer. The next development line after `v0.9.x` must not be
  numbered `v0.5.x`.
- No release requires simultaneous tags in every reference-client repository.
- A distribution release may publish a known-good combination without making
  all components share the same version.
- Core API compatibility, event compatibility, migration compatibility, and
  adapter compatibility are checked separately.
- Release notes distinguish kernel changes, official-module changes, profile
  changes, adapter-contract changes, and deployment changes.
- The capability matrix is generated or verified from the released artifacts and
  configuration schema rather than maintained only as prose.

## Capacity, and what that forces

This roadmap described eight milestones without saying who would build them or
over what horizon. That omission is not neutral: it makes every milestone read
as equally urgent and lets the plan promise a distribution product, an
operations product, and a re-architecture of the financial domain in the same
breath as the domain work itself.

`SECURITY.md` states the real constraint plainly: this is a small project
maintained by one person. M1 through M4 alone rewrite the ownership, funding,
acceptance, and identity models of a double-entry financial system that already
works, under an expand-migrate-contract discipline, with RLS and reconciliation
evidence per step. That is the substance of the roadmap and it is measured in
person-years, not weeks.

Two consequences follow, and the rest of this document has been adjusted for
them.

**Sequence rather than breadth.** M1 through M4 are the work that cannot be
avoided, because they change contracts and stored state. M5 through M7 package
that work, and packaging scales with how many shapes it must support. Where this
roadmap previously listed every shape worth eventually supporting, it now lists
the smallest set that proves the model generalizes, and names the rest as
post-1.0.

**A generalized 1.0 is not this month's release.** Anyone reading this should
expect the `0.x` line to continue through the domain milestones. Cutting a 1.0
before M4 settles would freeze exactly the assumptions the audit says must move,
which is the mistake a premature `v1.0.0` already made once on 2026-09-07.

## Milestone overview

| Milestone | Outcome | Depends on |
| --- | --- | --- |
| M0 | One truthful product, release, and architecture baseline | Current `v0.9.1` state |
| M1 | Program, policy, participant, and resource-ownership foundation | M0 decisions |
| M2 | Merchant acceptance and multi-party settlement | M1 participant model |
| M3 | Channel-neutral payment and general value lifecycles | M1-M2 |
| M4 | Unified identity, service principals, events, and data governance | M1 resource model; M3 events |
| M5 | Official-module composition, profiles, and adapter contracts | M1-M4 stable internal contracts |
| M6 | Low-friction onboarding, deployment, operation, and upgrades | M5 composition model |
| M7 | Reference-client migration, conformance, staging, and 1.0 release | All previous milestones |

Each milestone must include schema migration, RLS, audit, reconciliation,
documentation, and upgrade evidence. Security work is not deferred to M7.

## M0 — Establish one truthful baseline

### Objective

Remove ambiguity about what the current product is, what 1.0 will promise, and
which documented records are current.

### Required decisions and work

1. Adopt this roadmap and its audit as product-direction records.
2. Define 1.0 as generalized adoptability plus API and upgrade stability, not as
   feature completeness for the original supermarket alone.
3. ~~Replace the non-monotonic future `v0.5.0` naming with a monotonic pre-1.0
   line or a separate deployment-certification label.~~ **Done, by the second
   option.** `v0.5.0` is retired as a version. Deployment certification is now a
   label applied to a release, written `v0.9.1 (deployment certified)`, so
   certifying somebody's infrastructure and committing to API stability are no
   longer the same claim and neither waits for the other. `VERSIONING.md`
   records why the old row was a mistake in kind and not only in ordering.
4. ~~Publish one current capability and limitation matrix for all components.~~
   **Done.** `docs/CAPABILITIES.md` covers every capability in six states, of
   which the three that carry the weight are implemented, reference
   implementation, and operator responsibility. Nothing is marked implemented
   for having code; where a guarantee rests on a database constraint or a
   withheld privilege rather than on application code, the row says which.
5. ~~Split current documentation from archived plans, handoffs, reviews, and
   superseded research.~~ **Done.** Six working documents are local-only and the
   rest of `docs/` is published behind the index in `docs/README.md`. The split
   is enforced rather than described: CI fails on an unresolvable ADR citation
   or a broken relative link, which is what stops the published set from
   silently referring to the archived one.
6. ~~Extract ADRs into independently linkable records~~ **Done, by the second
   option.** `docs/DECISIONS.md` is published and CI fails any `ADR-nnn` cited
   in a tracked file that has no entry in it. Splitting 57 records into separate
   files would break 214 existing citations across the source to produce the
   same resolvability the index already provides, so it is dropped rather than
   deferred.
7. ~~Reconcile current release/tag claims across backend and client
   repositories.~~ **Done.** All four repositories are at `v0.9.1` with
   byte-identical compatibility manifests, and the contract pin is validated in
   CI in each of them rather than asserted. The `v1.0.0` that was cut and
   retracted on the same day is recorded in `VERSIONING.md` instead of erased.
8. Record the current OpenAPI contract defects and client-validation blind spots
   as release blockers. **Partly done.** Money was declared as `number/double`
   on 53 schema fields, and the portal's generated client consequently held all
   of its amounts in binary `double`, including three explicit `(double)amount`
   narrowing casts on the funding path. That is corrected: the contract declares
   `number/decimal`, the pin is rolled through all four repositories, and the
   portal is decimal from parse to wire. The compatibility gate could not see
   this class of change and now compares type and format. Enum serialization
   differences and a published conformance suite for custom clients remain.
9. Create a cross-repository threat model covering human, service, POS, partner,
   merchant, claim, webhook, adapter, and deployment trust boundaries.
10. Define the exact compatibility surfaces owned by core, clients, adapters,
    events, profiles, and the distribution manifest.

### Exit gate

- One document names the current released versions and deployment evidence.
- Every published status statement agrees with the repository tags and manifests.
- Historical documents are visibly archived and absent from the primary adopter
  reading path.
- 1.0 compatibility promises are written for HTTP, events, configuration,
  profiles, adapters, and migrations.
- The threat model and unresolved high-risk decisions are public.
- No implementation milestone depends on an undefined meaning of tenant,
  program, issuer, merchant, or service principal.

### Where M0 stands, 2026-09-07

The first three gate lines are met. `RELEASE_COMPATIBILITY.json` names the
current release, `VERSIONING.md` states what the number promises and what the
retracted `v1.0.0` was, and the published documentation set agrees with the tags
because CI checks that it does rather than because somebody read it over.

Three lines are open, and they are open for different reasons.

The compatibility promises cover HTTP and migrations but not events, profiles or
adapters, because none of those surfaces exists yet. That line cannot close
before M5 defines what an adapter contract is, and writing promises about them
now would be inventing a surface to promise about.

The threat model is unstarted. It is the one M0 item that needs neither a
decision nor a later milestone, so it is the next thing to do here.

The last line closes in M1 by construction: program, participant and issuer get
their definitions there. It is listed under M0 because M0 is where the absence
of those definitions was noticed, not because M0 can supply them.

## M1 — Programs, policies, and participants

### Objective

Replace the original universal corporate topology with stable general concepts
while preserving the existing corporate-rewards journey as migrated data and a
supported profile.

### Required domain model

Introduce:

- `Program`;
- immutable `ProgramPolicyVersion`;
- `Participant` backed by or associated with an organization;
- participant roles such as operator, program owner, issuer, funder, distributor,
  merchant, payee, and beneficiary;
- program-to-participant assignments;
- explicit resource ownership and visibility scope;
- issuer and liability-owner attribution;
- program currency and amount rules;
- and an immutable policy reference on every instrument.

The first policy model must cover:

- validity and optional/no expiration;
- expiration disposition;
- reloadability;
- transfer and sharing permissions;
- ownership strategy;
- merchant, location, and channel acceptance;
- transaction and period limits;
- refund and cancellation policy;
- credential and authorization lifetime bounds;
- terms and privacy references;
- and branding/locale references.

### Migration strategy

- Create one default program for every existing tenant root.
- Map the tenant root to operator, program-owner, issuer, funder, and liability
  roles where the existing model currently treats them as one party.
- Map existing cards to an immutable policy version reproducing their exact
  current expiry, transfer, sharing, ownership, and refund behavior.
- Preserve every existing identifier, ledger account, entry, audit record, and
  idempotency outcome.
- Add and backfill new references before making them mandatory.
- Verify old and new reports reconcile to identical per-currency totals.
- Do not rewrite historical ledger or audit records to simulate the new model.

### Security requirements

- RLS tests cover unrelated operators, programs, issuers, funders, merchants,
  distributors, and beneficiaries.
- Participant roles grant no authority without an explicit resource assignment.
- A participant holding several roles cannot widen one role's scope through
  another.
- Platform authority remains distinct and audited.
- Policy changes require named permission and create immutable audit evidence.

### Exit gate

- The original supermarket journey passes unchanged through a migrated default
  program.
- Two programs under one operator can have different policies without data or
  permission leakage.
- A funder, issuer, distributor, and merchant can be separate organizations.
- A card issued under policy version 1 retains that behavior after version 2 is
  published.
- No-expiry and expiring programs are both representable.
- New adopters do not need `CorporateCredit` or employee terminology to describe
  a basic program.

## M2 — Merchant acceptance and settlement

### Objective

Make single- and multi-merchant acceptance first-class without weakening the
current payment and reconciliation guarantees.

### Required domain model

Introduce:

- first-class `Merchant` participant behavior;
- `AcceptanceLocation`;
- merchant-owned acceptance clients and devices;
- acceptance-client grants and retirement;
- merchant clearing and payable account roles;
- settlement allocations;
- platform, distributor, or merchant fee lines;
- settlement adjustments;
- merchant-scoped payment and refund visibility;
- and settlement statements or export-ready records.

Preserve `storeReference`, receipt reference, and other external identifiers as
integration data, not as substitutes for owned resources.

The current platform-scoped per-currency settlement account remains supported by
the simple closed-loop profile. It stops being the universal settlement model.

### Financial requirements

- Every capture records who accepted value and who is owed value.
- Capture allocations balance exactly to the captured amount per currency.
- Fees and adjustments use explicit accounts and immutable postings.
- Refunds reverse the correct merchant, fee, and settlement positions according
  to the program policy.
- A settlement report is derived from ledger-authoritative transactions and
  cannot become a competing source of truth.

### Security requirements

- A merchant sees only its locations, credentials, payments, refunds, and
  settlement positions.
- A merchant cannot mint value merely because it can accept it.
- An issuer or funder does not automatically acquire detailed cardholder-spending
  visibility.
- Device enrollment, rotation, and retirement remain one-time, auditable, and
  immediately enforceable.
- Platform operators use explicit cross-merchant permissions rather than implicit
  global access.

### Exit gate

- A single supermarket continues using the simple settlement profile.
- A district alliance can onboard two unrelated cafes and reconcile what each is
  owed.
- One cafe cannot read or refund the other cafe's payment.
- A platform fee and merchant payable can be recorded and reversed without an
  unbalanced transaction.
- Merchant reporting agrees with platform clearing and ledger totals.

## M3 — Channel-neutral payments and general value operations

### Objective

Retain the safe retail payment flow while making POS presentation one adapter
over a general payment and stored-value lifecycle.

### Payment intent

Introduce a channel-neutral payment intent that binds:

- program;
- cardholder or authorized instrument context;
- merchant and optional location;
- external order or transaction reference;
- requested amount and currency;
- structured amount components;
- eligible amount determined by policy;
- intended settlement allocations;
- authorization deadline;
- capture mode;
- channel;
- and idempotency identity.

QR and numeric payment credentials resolve into the intent. Online checkout,
future mobile clients, or other presentation mechanisms use the same core
without impersonating a POS terminal.

### Payment lifecycle

Support:

- creation;
- authorization or reservation;
- status lookup and lost-response recovery;
- bounded increase or decrease where the policy permits it;
- one or more bounded captures;
- finalization;
- cancellation and expiry;
- partial and full refund;
- and explicit refund destination.

Every program declares which subset and limits it supports. The retail profile
retains short credentials, a two-minute hold, opt-in partial approval, and one
capture unless deliberately configured otherwise.

### Structured amount components

Represent bounded semantic components such as:

- merchandise;
- food;
- service or delivery fee;
- tax;
- tip;
- discount;
- and custom purpose code.

Open Giftcard does not calculate catalogue prices or tax. It records caller
intent, evaluates program eligibility, persists the accepted decision, and
allocates the resulting capture.

### General value operations

Replace corporate allocation as the only normal source of value with explicit,
auditable operations for:

- issue;
- load;
- reload;
- promotional or sponsor grant;
- scheduled grant;
- adjustment;
- transfer where allowed;
- expiration or cancellation disposition;
- and refund to original, replacement, unclaimed-liability, or external
  destination according to policy.

Provider-specific consumer payment collection remains an adapter. The core
records confirmed funding; it does not become a card-acquiring platform.

### Exit gate

- The existing POS QR/numeric journey remains safe and compatible with its
  profile.
- A food-delivery order can authorize, wait for restaurant acceptance, capture a
  permitted final amount, cancel, and refund without pretending to be a lane.
- A program can exclude tips or delivery fees while allowing the food subtotal.
- A reduced total can be captured without reauthorization.
- An increase or additional capture is impossible unless the program, caller,
  authorization ceiling, and available value all allow it.
- A refund after the original card becomes terminal follows an explicit policy
  rather than reaching an undefined state.
- Reload and sponsor grants remain balanced, idempotent, and attributable.

## M4 — Identity, service principals, events, and data governance

### Objective

Remove the assumption that every actor is either a built-in human, a bespoke POS
client, or a bespoke reseller client. Give external systems a safe, observable
integration boundary.

### Human identity

Support a common identity-subject model with profiles for:

- built-in email or phone account;
- issuer-specific identity realm;
- externally trusted subject;
- anonymous or bearer ownership with strict limits;
- and custodial or dependent ownership.

Complete the built-in account lifecycle:

- verification;
- password recovery;
- contact addition and change;
- account linking and conflict resolution;
- administrative recovery with strong audit;
- session and factor management;
- and an appropriate MFA baseline for privileged staff.

OIDC, SAML, and SCIM providers may be external adapters, but the core subject
model must support them without schema forks.

### Service principals

Introduce one service-principal and grant model for:

- merchant servers;
- POS devices;
- distributors;
- issuer automation;
- bulk workers;
- support tools;
- and trusted adapters.

It must cover scope, grants, credential expiry, rotation, retirement, quotas,
audit attribution, and live authorization lookup. New integration types must not
require new privileged branches in the ledger writer.

### Durable integration events

Publish versioned event envelopes with:

- immutable event identifier;
- event type and schema version;
- aggregate/resource reference;
- program and permitted participant scope;
- occurred time;
- correlation and causation identifiers;
- idempotency/deduplication identity;
- privacy-classified payload;
- signature metadata;
- and ordering information where guaranteed.

Webhook subscriptions need scoped event selection, signed delivery, bounded
retry, replay, secret rotation, delivery history, dead-letter visibility, and a
test endpoint.

Integration events are separate from recipient notification messages even when
they share the durable outbox infrastructure.

### Data governance

Define:

- data inventory and retention classes;
- data-subject export;
- contact correction;
- pseudonymization or anonymization preserving financial evidence;
- terms and consent-version attribution where applicable;
- auditable support access;
- safe operational and accounting exports;
- and tenant/program deletion or closure behavior.

### Exit gate

- A headless issuer can fund, issue, query, and reconcile through a scoped service
  principal without a human login.
- A merchant service principal cannot acquire issuer or platform authority.
- An external identity subject can own and use an instrument without duplicating
  the adopter's account system.
- Password recovery and staff MFA journeys are implemented for the built-in
  profile.
- A webhook consumer can lose a response, receive a retry, deduplicate it, and
  request replay.
- Data export and anonymization preserve ledger balance and audit attribution.

## M5 — Module composition, profiles, adapters, and client compatibility

### Objective

Make optionality real without multiplying editions, release branches, or
financial plugin risk.

### Official module contract

For every optional official module define:

- capability identifier and version;
- required kernel capabilities;
- permissions;
- routes and OpenAPI document;
- migrations;
- workers;
- configuration schema;
- program-policy fields;
- audit events;
- integration events;
- enable, disable, and re-enable behavior;
- and active-state shutdown rules.

Disabling a module with active shares, holds, batches, or invitations must have a
defined drain, freeze, cancel, or finish policy. It must never strand or erase
value.

### Initial official profiles

Build and continuously test:

- `minimal-single-merchant`: one operator/issuer/merchant, built-in identity,
  cardholder, basic acceptance, refunds, and reconciliation;
- `community-alliance`: one program with several merchants, merchant enrollment,
  clearing, and settlement statements;
- `corporate-rewards`: hierarchy, corporate funding, employee distribution,
  optional sharing, and bulk upload;
- `retail-pos`: short-lived presentation, device enrollment, counter hold,
  partial approval, capture, refund, and reports;
- `reseller-epin`: distributor service principal, prepaid funding preset,
  unbound claim, quotas, and clawback;
- `headless-api`: no required official browser clients;
- `multi-merchant`: merchant and location administration plus clearing;
- `hardened-enterprise`: HA-oriented infrastructure, external identity, external
  custody, and stronger operational evidence.

Every profile uses the same core artifacts and migration history.

### Adapter contract and catalogue

Publish:

- language-neutral adapter contracts;
- manifest and configuration schemas;
- contract test kits;
- local simulators;
- installation and removal rules;
- secret rotation and health behavior;
- compatibility validation;
- and trust labels such as official, verified community, and unverified.

Removing an adapter must be refused while a required active program depends on it
unless a replacement or safe disable policy is selected.

### Client compatibility

- Generate clients or validate actual serialized requests and responses against
  OpenAPI.
- Correct decimal schemas and enum representation before the stable baseline.
- Ensure unknown additive response fields and permitted enum evolution do not
  break official clients.
- Publish a conformance kit for third-party portals, cardholder applications,
  merchant integrations, and POS bridges.
- Version official clients independently and test declared backend ranges.

### Exit gate

- Every supported profile starts and completes its primary journey in CI.
- Disabling each optional module removes its active surface without changing
  core financial behavior.
- A profile can enable an official module after deployment through a tested
  migration and configuration path.
- An adapter can be installed, configured, health-checked, rotated, replaced, and
  removed without editing core source.
- A headless backend release does not require simultaneous portal, cardholder, or
  POS tags.

## M6 — Installation, onboarding, operation, and upgrades

### Objective

Make secure deployment achievable for a small organization with two technically
capable maintainers, without pretending that financial infrastructure is
zero-maintenance.

### Distribution tooling

Provide a signed `open-giftcard` installer/management CLI or equivalent tool with
commands such as:

```text
open-giftcard init --profile community-alliance
open-giftcard doctor
open-giftcard start
open-giftcard stop
open-giftcard backup
open-giftcard restore-test
open-giftcard upgrade --check
open-giftcard upgrade
open-giftcard adapter install <name>
open-giftcard adapter test <name>
open-giftcard evidence collect
```

The tooling must:

- install from signed release artifacts rather than requiring Git clones;
- generate strong secrets;
- produce explicit configuration and a lock manifest;
- create least-privilege database roles;
- run explicit migrators;
- configure protected key storage;
- check ports, DNS, TLS, disk, and dependencies;
- refuse insecure public defaults;
- automate backups and restore tests;
- show exactly what an upgrade changes;
- and preserve redacted diagnostic evidence.

### First-run business wizard

The portal should guide an operator through:

1. deployment administrator creation;
2. product name, domain, logo, colors, support, terms, and privacy links;
3. program name, currency, issuer, funder, expiry, reload, sharing, and refund
   policies;
4. initial merchants and locations;
5. staff invitations and roles;
6. notification and identity adapters;
7. backup destination;
8. POS or merchant-server enrollment;
9. test card or grant;
10. authorization, capture, refund, settlement, and reconciliation acceptance;
11. production-readiness review.

The wizard uses business language and reveals technical detail only when needed.
It never creates weaker security rules merely to make a step disappear.

### Secure device enrollment

Replace manual POS secret copying with a short-lived, single-use enrollment
protocol:

1. Authorized merchant staff request a device enrollment.
2. The portal displays a bounded QR or code.
3. The device exchanges it once over authenticated transport.
4. The backend binds the device to merchant, location, program capability, and
   grants.
5. The device stores protected credentials.
6. Enrollment finishes only after a readiness and test-transaction check.
7. Rotation uses replacement plus audited retirement.

### Supported deployment tiers

#### Demo

- one command;
- seeded data;
- local HTTP only;
- disposable;
- no production claim.

#### Community Production

- supported single-node topology;
- automatic TLS through an included reverse-proxy configuration;
- one PostgreSQL service with isolated databases and roles;
- backend, portal, and cardholder services;
- explicit migrator jobs;
- encrypted automatic backups;
- restore-test command;
- SMTP or selected notification adapter;
- health and operations dashboard;
- one upgrade workflow;
- optional per-location POS bridge;
- advanced audit custody disabled unless selected;
- no HA claim.

#### Hardened Production, documented but not supported at 1.0

- managed or replicated PostgreSQL;
- multiple application replicas;
- external secret manager;
- central logs and metrics;
- managed identity provider;
- external audit custody and immutable witness;
- controlled ingress;
- tested recovery;
- and formal deployment evidence.

This was listed as a supported tier. It is now documented as a target an
operator can build toward, with the project supplying the properties it can
actually certify from source, and no claim that the project has run this shape.

The reason is that this project has never been deployed anywhere, once. Going
from zero deployments to three supported tiers in one release would repeat, at
larger scale, the error the release-readiness gate already warns about: a source
artifact does not prove an operator's controls. Several items in this tier are
also explicitly outside what the project can certify at any version, including
managed identity, external custody, and controlled ingress.

Promote it to supported when one named environment has run it and the evidence
is recorded, which is the `v0.5.0` line, not the 1.0 one.

The tiers differ in availability, custody, and operational assurance. They do not
differ in ledger, authorization, tenant isolation, audit immutability, secret
handling, or migration correctness.

### Operator dashboard, reduced for 1.0

At 1.0 this is a runbook plus the readiness and metrics surfaces that already
exist, not a built interface. The backend already exposes bounded OTLP metrics,
a readiness probe that names modules behind the build, and six alert rules; an
operator can answer most of the questions below from those today.

A built dashboard is eleven distinct signal classes with their own refresh,
authorization, and failure behaviour. That is a product, and it competes
directly with the domain milestones for the same single maintainer.

The list below stays as the specification for what the runbook must let an
operator answer, and as the eventual dashboard scope:

- service and dependency readiness;
- migration state;
- notification and webhook failures;
- worker backlog and dead letters;
- last successful backup and restore test;
- reconciliation findings;
- expiring credentials and certificates;
- disabled or unhealthy merchant devices;
- adapter health;
- available upgrades and compatibility impact;
- and links to actionable runbooks.

### Exit gate

- A new evaluator reaches a seeded full product from released artifacts with one
  command.
- Two technically capable maintainers can configure the Community Production
  profile without editing source, SQL, Compose YAML, or reverse-proxy rules.
- The wizard completes a real issue, payment, refund, merchant settlement, and
  reconciliation journey.
- Backup and isolated restore testing are one documented command each.
- Upgrade preflight detects incompatible clients, adapters, profiles,
  configuration, and pending migrations before modifying production state.
- No production-shaped profile starts with default secrets, public HTTP, missing
  durable keys, or an unavailable required adapter.

## M7 — Reference clients, migration, conformance, and 1.0 release

### Objective

Prove the generalized platform across sectors and publish a stable release whose
promises match its evidence.

### Reference-client migration

- Update the portal to administer programs, participants, merchants, policies,
  service principals, adapters, and settlement positions.
- Present Corporate Rewards, Sharing, Bulk, Reseller, and POS workspaces only
  when enabled.
- Update the cardholder client for program branding, policy disclosure, identity
  realms, optional/no expiry, reload, and channel-neutral payment intents.
- Keep the POS application a reference bridge over the general payment contract.
- Preserve the original corporate/supermarket demonstration as one supported
  profile and regression journey.
- Publish at least one headless integration sample that does not depend on any
  official browser client.

### Cross-sector acceptance suite

The release must prove:

1. **Single merchant:** A cafe can configure, brand, fund, issue, accept, refund,
   and reconcile without source edits.
2. **Food delivery:** An order service can authorize, wait, adjust within policy,
   capture, cancel, refund, and receive webhooks without impersonating a POS lane.
3. **Community alliance:** Two unrelated merchants can accept the same program
   and reconcile separate payable positions.
4. **Restricted aid:** A program can limit acceptance by merchant, location,
   purpose, amount, and time without exposing unnecessary beneficiary spending
   detail.
5. **Campus/external identity:** An externally authenticated subject can receive
   and use restricted value without a duplicate built-in account.
6. **White-label SaaS:** One operator can host two programs with separate brands,
   policies, identity realms, merchants, and data visibility.
7. **Headless issuer:** A scoped service principal can fund, issue, query,
   reconcile, and consume events without a human session.
8. **Original supermarket:** Corporate allocation, employee bulk distribution,
   optional sharing, QR/POS redemption, refund, audit, and reconciliation remain
   available through the official profile.

### Security and reliability release gates

- Full real-PostgreSQL suite across every supported profile.
- RLS matrix across operators, programs, participants, merchants, beneficiaries,
  human users, devices, service principals, workers, and platform staff.
- Balanced-ledger and reconciliation property tests for every value operation and
  settlement allocation.
- Concurrency tests across payment, sharing, reload, refund, expiration, and
  policy transitions.
- Threat-model review with all critical findings closed or explicitly blocked.
- Dependency and code scanning across all released repositories and images.
- Adapter and webhook abuse, replay, signing, and secret-rotation tests.
- Upgrade from the public `v0.9.1` data model through every intermediate
  migration to the 1.0 candidate.
- Client and adapter compatibility checks against declared ranges.
- Backup, restore, restart, replica handoff, worker recovery, and rollback
  evidence.
- Human accessibility, responsive, localization, and physical POS review.
- Named staging deployment of the exact candidate artifacts.
- No unresolved contradiction in the capability, versioning, security,
  deployment, or support documents.

### 1.0 publication gate

Publish 1.0 only when:

- `/api/v1` represents the generalized program, participant, instrument,
  merchant, payment, and integration model;
- exact API and event compatibility checks are green;
- the upgrade path has migrated real populated `v0.9.1` data without losing or
  rewriting value;
- supported profiles and adapter contracts are versioned and documented;
- at least the Community Production and Hardened Production deployment paths
  have named evidence appropriate to their claims;
- the eight cross-sector journeys pass;
- official client compatibility ranges are published;
- the capability and limitation matrix matches the artifacts;
- and release notes clearly distinguish core promises from optional modules,
  adapters, reference clients, and operator responsibilities.

## Migration and compatibility strategy throughout the roadmap

### Expand, migrate, contract

Every schema-changing milestone follows:

1. **Expand:** Add new tables, account roles, references, and nullable columns
   without removing old behavior.
2. **Migrate:** Backfill deterministically, run reconciliation, and allow old and
   new reads to compare.
3. **Converge:** Move official clients and write paths to the new model.
4. **Prove:** Exercise upgrade, concurrency, RLS, reconciliation, backup, and
   restore.
5. **Contract before 1.0:** Remove obsolete pre-stable surfaces only after all
   official consumers have moved and release notes name the break.

The project is still `0.x`, so deliberate breaking API corrections are permitted.
They should nevertheless carry migration guidance and compatibility evidence;
the absence of a stability promise is not permission to discard adopter data.

### Historical integrity

- Existing ledger entries and audit records remain byte-for-byte historical
  evidence wherever possible.
- New attribution tables may interpret old transactions through recorded legacy
  mappings rather than rewriting them.
- Backfills use deterministic identifiers and idempotent migrations.
- Every migration records pre- and post-migration reconciliation totals.
- A migration that cannot map a legacy state stops and reports it; it does not
  guess.

### Legacy profile

The existing deployment becomes a named `corporate-rewards + retail-pos` profile.
Legacy API translations may exist during `0.x` migration, but the stable 1.0
contract must expose the generalized model. Do not preserve a faulty abstraction
forever solely because it appeared in a pre-1.0 endpoint.

## Continuous verification matrix

Every milestone and future release should run these layers:

| Layer | Required evidence |
| --- | --- |
| Unit and architecture | Module boundaries, domain invariants, policy evaluation, serialization |
| PostgreSQL integration | RLS, transactions, migrations, idempotency, concurrency, balancing |
| Profile composition | Start, migrate, readiness, primary journey, disabled-feature behavior |
| API and events | OpenAPI/schema validation, backward compatibility, webhook conformance |
| Client compatibility | Actual request serialization and response handling across supported ranges |
| Security | Threat-model regression, secret handling, replay, quotas, dependency and code scans |
| Recovery | Backup, restore, worker restart, lost response, credential rotation, replica handoff |
| Human acceptance | Accessibility, localization, mobile, merchant/POS workflow, operational clarity. Per release rather than per milestone: this layer needs a person driving a browser, and requiring it at every milestone exit either stalls the milestone or turns the check into a formality |

No optional profile may skip the financial, RLS, API, migration, or recovery
layers. It may omit tests only for a capability that is genuinely absent.

## Maintenance constraints

The following constraints prevent generalization from becoming an unmaintainable
framework:

- Do not introduce microservices merely to make modules look optional.
- Do not implement arbitrary policy scripting for 1.0.
- Do not load third-party code into the financial process.
- Do not create sector-specific core tables when participant roles, policy,
  bounded metadata, or an adapter can express the need.
- Do not make database tables an external integration contract.
- Do not maintain separate release branches or binaries for profiles.
- Do not make every provider an official repository; define a conformance contract
  and maintain only reference adapters the project can support.
- Do not make portal visibility an authorization mechanism.
- Do not allow configuration to retroactively change issued-instrument policy.
- Do not promise full SaaS control-plane billing, KYC, tax, loyalty, ordering, or
  retail functionality as part of the gift-card kernel.

## Explicitly after 1.0

The following do not block the stable generalized foundation:

- foreign-exchange conversion inside one instrument;
- offline or store-and-forward authorization;
- NFC and wristband implementations;
- Apple or Google wallet passes;
- native mobile applications and push delivery;
- full loyalty earning engines;
- complete catalogue, stock, tax, receipt, or cash-drawer systems;
- food ordering, courier, or booking workflows;
- advanced fraud machine learning;
- provider-specific KYC, AML, payout, tax, or regulatory filing products;
- beneficiary case management;
- and student-information systems.

The required 1.0 work is to make these future integrations possible through
stable principals, events, program policies, funding operations, and adapters,
not to build each industry around the kernel.

## Roadmap decision checklist

Before implementation begins on each milestone, its ADR set must answer:

- What invariant is universal, and what behavior belongs to a program policy?
- Which participant owns the resource and which participants may act on it?
- Which ledger accounts and balanced postings represent the value movement?
- Which RLS policy enforces the boundary independently of application filtering?
- Which human or service permission authorizes it?
- Which idempotency identity protects retries?
- Which audit and integration events result?
- Which privacy class applies to every new field?
- How does existing `v0.9.1` data migrate?
- How is the operation reconciled?
- What happens when its module or adapter is unavailable or disabled?
- Which supported profiles exercise it?
- Which public compatibility surface changes?
- What evidence closes the milestone?

## Feasibility review, 2026-09-07

This roadmap was reviewed against the codebase rather than read on its own
terms, and edited where a claim was not achievable by the people who would have
to achieve it. The architecture was not changed. Every edit reduced scope or
corrected a statement that had gone stale; none altered the model.

**Kept, because it is right.** The composition model is the strongest part of
this document and should not be revisited. Compiling the kernel and official
modules together, treating profiles as versioned configuration over one artifact
set rather than as editions, and pushing third-party integrations out of
process, are the three decisions that stop a generalized platform becoming an
unmaintainable framework. The three levels of optionality, and in particular
binding an issued instrument to the policy version it was issued under, is the
insight the whole model rests on. The maintenance constraints read like they
were written by someone who has watched this go wrong before.

**Reduced, on capacity grounds.** Supported profiles went from eight to three,
deployment tiers from three supported to two supported plus one documented, and
the operator dashboard from a built interface to a runbook over surfaces that
already exist. None of these is a disagreement about direction. Each is the same
observation: the verification matrix makes every supported shape a standing test
and support burden, and one maintainer cannot carry eight of them without
carrying all of them badly.

**Corrected, because the codebase had moved.** Two M0 items were already done or
partly done. The ADR extraction is unnecessary because the published index plus
a CI check already gives the resolvability it was asking for. The contract
defects item is half closed, because the money declaration was fixed the same
day this review was written.

**Left standing as a known risk.** M1 through M4 rewrite the ownership, funding,
acceptance and identity models of a working double-entry system. Expand, migrate,
converge, prove, contract is the right discipline and the historical-integrity
rules are sound, in particular that a migration which cannot map a legacy state
stops and reports rather than guessing. It remains the riskiest work this project
has attempted, and the roadmap is right that security cannot be deferred to M7.

**One thing this review could not settle.** The roadmap assumes the generalized
model is worth the cost, and that assumption is a product judgement rather than
an engineering one. The audit makes a strong case that freezing the current
contract would force adopters to fork. It does not establish that adopters exist
who want the generalized shape badly enough to justify person-years of rework on
a system that currently works. That question should be answered before M1 starts,
because M1 is the point of no return: after it, the old model is gone and the new
one is not yet proven.

## Final success condition

The roadmap is complete when Open Giftcard can honestly say:

> One secure, maintainable Open Giftcard core supports corporate rewards,
> single-merchant programs, multi-merchant communities, headless issuers,
> restricted-value programs, online commerce, reseller channels, and retail POS
> through explicit programs, policies, participants, modules, profiles, and
> adapters—without separate editions and without weakening its ledger,
> authorization, tenant isolation, audit, or upgrade guarantees.

