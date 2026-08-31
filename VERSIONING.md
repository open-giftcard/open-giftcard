# Versioning and release promises

What each version number commits this project to, and what it deliberately does
not. `RELEASE_READINESS.md` is the gate for the current candidate; this file is
the longer-lived statement of what the numbers mean.

The four repositories version in lockstep through `RELEASE_COMPATIBILITY.json`.
A version names a set of four compatible artifacts and one backend OpenAPI
document, never one repository on its own.

## The version line

| Version | What it means |
| --- | --- |
| `0.x` | No promises. The API, the schema, and the configuration may all change. Pin by commit. |
| `v0.9.0` | The source is complete and self-verifying. Every promise 1.0 will make is either enforced in CI or explicitly withdrawn, and the documentation to check that is published. Still 0.x: no stability promise, and nothing has been deployed. |
| `v0.5.0` | It runs. The candidate has been deployed to a named environment and the evidence is recorded. Still no stability promise. |
| `v1.0.0` | It is stable and adoptable. The three promises below take effect. |
| `1.x` | Additive change only, under the deprecation policy below. |
| `2.0.0` | Reserved for a breaking API change, served as `/api/v2` alongside `/api/v1`. |

`v0.5.0` and `v1.0.0` are independent. Certifying a deployment and committing to
stability are different claims, they are blocked on different things, and
neither has to wait for the other.

`v0.9.0` sits outside that pair deliberately. It is the first tag this project
has ever cut, and it exists because two of the checks below cannot work without
a previous release to compare against: the API compatibility gate needs a
baseline, and the upgrade job needs an earlier schema. Tagging is what lets
those gates start doing their job, so it comes before the release it protects
rather than after.

What `v0.9.0` says: the code is finished enough to hold still, the promises are
written down, and the machinery that will enforce them is running. What it does
not say: that anything has been deployed, that the API will not change, or that
an upgrade path has been exercised. Those are `v0.5.0` and `v1.0.0`, and both
are still open.

## What 1.0 promises

### 1. The HTTP API is stable within 1.x

`/api/v1` will not break in any 1.x release. Concretely, these are allowed:

- new endpoints
- new optional request fields
- new response fields
- new enum values in a response, where the field is already documented as open
- relaxing a validation rule

and these are not, until `/api/v2`:

- removing or renaming an endpoint, a request field, or a response field
- making an optional request field required
- narrowing an accepted value set, including adding a currency allow-list
- changing the status code returned for an existing condition
- changing a problem type URI

Problem type URIs under `https://giftcard.example/problems/` are stable
identifiers, not documentation links. `src/api.ts` compares them literally.
They are deliberately not configurable and will not be renamed within a major
version.

**How it is enforced.** `scripts/Test-ApiCompatibility.ps1` compares the served
OpenAPI document against the accepted baseline in `contracts/` and fails on any
change in the second list. CI runs it in the `compose` job, against the document
a real running instance serves rather than a generated file. Additive change
passes; anything outside either list is reported as a warning, because the
promise is the list.

The baseline moves only at a major release, and never to make a failure go away.
`contracts/README.md` says so where someone tempted to move it will be reading.

One item on the forbidden list is not machine-checked: problem type URIs do not
appear in the OpenAPI document, so nothing compares them. They are compared
literally by client code, so a change would break clients silently. Treat them
as frozen by review until a check exists.

### 2. Upgrading within 1.x is safe

- Migrations are forward-only. A 1.x database is never downgraded.
- Any 1.x release applies cleanly on top of any earlier 1.x database.
- `GET /health/ready` returns 503 naming the modules that are behind whenever
  the schema does not match the running build, so an instance refuses traffic
  rather than serving a partly migrated database.
- Application rollback to the previous artifact is supported only through the
  documented compatibility probe in `docs/DEPLOYMENT.md`, and only with
  compensating controls for any security feature the older artifact lacks.

**How it is enforced.** The `upgrade` job in CI brings up the accepted baseline,
populates it through the demonstration seed, then applies this build's
migrations over that same database volume and requires `/health/ready` to answer
without naming a module as behind. It then asserts the seeded value survived and
that every ledger transaction still balances per currency, so a migration that
half-applies or loses value fails the build rather than the first request.

The baseline is currently the accepted commit rather than a release tag, because
until `v0.9.0` no repository had one. Repoint it at `v0.9.0` in the change that
introduces the next schema migration, which is the first point at which the
comparison has anything to say.

**What a green tick here does and does not mean.** The job only exercises an
upgrade when the two commits differ in migrations. When they do not, it still
proves that this build serves a database the baseline populated, which is worth
having, but no migration was applied and nothing about migration safety was
tested. The job prints which of the two cases it ran and how many migrations it
applied, so the distinction is in the log rather than left to be inferred. On
its first run, against the accepted commit, the answer was zero: that commit and
this one carry the same migrations.

This is the honest limit of the check until the baseline is a release tag with
schema changes behind it. Read the printed count, not the tick.

### 3. An adopter can use it without forking the core

By 1.0, someone who has never spoken to the maintainer can:

- reach a working, populated system with documented credentials in one
  command, including the portal and cardholder, not just the API
- add a notification or audit custody provider by following published
  documentation
- read the architecture decisions behind every invariant the project claims

## What 1.0 does not promise

Stated plainly so nothing here is inferred from the number alone.

- **It is not a production warranty.** The certification boundary in
  `RELEASE_READINESS.md` still holds: this project cannot certify an operator's
  TLS termination, DNS, backup retention, incident staffing, KMS, HSM, or WORM
  provider. Those stay operator responsibilities at every version.
- **The database schema is not a stable interface.** The twelve module schemas,
  the module boundaries, and the internal contract types may change in any 1.x
  release. Anyone reading the tables directly is reading an internal surface.
  `Modules.Reporting` does exactly that and is inside the project for that
  reason.
- **Configuration keys are not frozen.** Names and defaults may change within
  1.x, with a changelog entry. The fixed 15 minute and 30 day session lifetimes
  are a deliberate invariant that fails startup when changed, and revisiting
  them is a 1.x change, not a breaking one.
- **SMS is absent by design.** Phone distribution, direct sharing, and bulk
  acceptance fail closed with `notification.channel.unconfigured` before any
  business state is committed. An operator adding an SMS adapter certifies it
  themselves.
- **The POS till is a reference client.** It exercises the payment contract and
  is versioned alongside the others. It is not retail software and physical
  counter-device certification is not part of any release claim.
- **Branding is not configurable.** An earlier revision of this file promised
  that an adopter could rebrand the product name, logo, colours, and sender
  identity through configuration by 1.0. No such configuration was ever built,
  and promising it unbuilt is worse than not promising it. Product naming is
  presently in source, in templates, and in the cardholder's string catalogue,
  so rebranding means editing them. This is planned after 1.0 and is deliberately
  not a 1.x compatibility commitment, because introducing it will add
  configuration keys rather than break any.

## Deprecation policy

Within 1.x, a surface may be deprecated but not removed.

1. Mark the operation or field `deprecated: true` in the OpenAPI document.
2. Record it in `CHANGELOG.md` under the release that deprecates it, naming the
   replacement.
3. Keep it working for at least two minor releases.
4. Remove it only in the next major version.

A deprecation is not a breaking change and does not require a major bump. A
removal always does.

## The 1.0 gate

Same four states as `RELEASE_READINESS.md`. Nothing is called 1.0 while a
required row is blocked.

| Area | Required evidence | State |
| --- | --- | --- |
| Contract stability | CI fails a breaking change to the served `/api/v1` document against the accepted baseline | Source verified: `scripts/Test-ApiCompatibility.ps1`, run against the served document in the `compose` job |
| Deprecation policy | Written, published, and referenced from `CONTRIBUTING.md` | Source verified: this file |
| Upgrade safety | CI applies the accepted baseline's migrations to a populated database, then this build's, and asserts readiness | Source verified: the `upgrade` job, which also asserts the seeded value survived and every ledger transaction still balances |
| Client contract direction | A client that omits a newly required field fails its own build, in all three clients | Blocked: route and field lists are hand-written, 48 literals in the portal and 18 in the cardholder |
| Demo reachability | One command reaches a portal login screen with seeded data, using credentials published in the README | Blocked: credentials are now published and the API is one command, but the portal and cardholder are not yet containerized |
| Provider extension | Published documentation walks an adopter through adding one notification and one audit custody provider | Source verified: `CONTRIBUTING.md`, honest that registration edits the host |
| Architecture decisions | Every ADR referenced from a public file resolves to a public document | Source verified: `docs/DECISIONS.md` is published and `scripts/Test-DocumentationReferences.ps1` fails any citation that does not resolve |
| Financial invariants | Balanced double entry, ledger-derived balances, idempotency, and forced RLS covered by the real-PostgreSQL suite | Source verified |
| Static analysis | CodeQL green on the released commit in all four repositories | Source verified |
| Release artifacts | Four versioned archives with SBOMs, checksums, and provenance attestation for the exact tagged commit | Source verified for the mechanism, unexercised on a real tag |
| Deployment evidence | Inherited from `v0.5.0`, referenced rather than repeated | Blocked: named environment |

## Why 1.0 is not defined as production certified

It would make the version number depend on infrastructure this project does not
own and cannot inspect. The honest split is that `v0.5.0` carries the deployment
evidence for one named environment, and `v1.0.0` carries the promises the
project can keep on its own: a stable API, a safe upgrade, and a system someone
else can adopt without forking it.
