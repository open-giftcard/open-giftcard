# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

`v1.0.0` is the current release and the first stable one. The `v0.9.x` tags
before it were the first this project ever published and made no stability
promise; see `VERSIONING.md` for what each number means and what it
deliberately does not. Local tags predating the open-source cleanup are not
usable, were never published, and are not listed.

## v1.0.0 - 2026-09-07

The first stable release. `VERSIONING.md` states what the number commits this
project to; in short, three promises now take effect.

**The HTTP API is stable within 1.x.** `/api/v1` will not break. A CI job diffs
the document a running instance serves against the accepted baseline and fails
on any change the policy forbids: a removed or renamed endpoint, request field
or response field, an optional request field made required, a narrowed accepted
value set, or a dropped status code. Additive change passes.

**Upgrading within 1.x is safe.** Migrations are forward-only, and CI applies
the previous release's migrations to a database populated through the
demonstration seed, then this build's over the same volume, and requires
readiness to answer without naming a module as behind. It then checks the
seeded value survived and that every ledger transaction still balances per
currency.

**An adopter can use it without forking the core.** One command brings up the
API, the operator portal and the cardholder application with a populated
tenant, and CI signs in through the portal with the credentials the README
publishes. Every architecture decision the source cites by number resolves to a
published document. Adding a notification or audit custody provider is
documented.

### Closed for this release

- The client contract direction is guarded in all three clients. The portal
  generates its backend client from the pinned contract at build time; the
  cardholder and POS assert their real serialised requests against the pinned
  schema in both directions, including its required fields.
- The full product is containerized. `docker compose -f docker-compose.yml -f
  docker-compose.full.yml up` brings up five more services alongside the API,
  and a CI job proves it from a clean checkout.
- The idempotency and retry contract is documented for integrators, including
  which conflicts are retryable and why the server does not retry them itself.

### Fixed

- The portal and cardholder pinned `rollForward: latestPatch`, so neither could
  build against a newer SDK feature band. That never showed in their own CI,
  which installs the exact pinned SDK, and appeared the first time the images
  were built. Both now match the backend and POS at `latestFeature`.
- The POS serialised-request check described itself as vacuous because the
  contract declared no required fields. It has been enforcing since the
  contract started declaring them; only the comment was wrong.

### Not in this release, by decision

Nothing has been deployed to a named environment, and `v1.0.0` deliberately
makes no deployment claim. That is `v0.5.0`, which remains open, and the gate
row asserting otherwise was removed with the reasoning recorded in
`VERSIONING.md`. SMS, managed audit custody and configurable branding remain
documented non-goals.

## v0.9.1 - 2026-08-31

### Fixed

- The release contract check reported a correctly tagged release as untagged,
  and failed CI on the `v0.9.0` commit in all four repositories. It looked for
  the tag only in the local working copy, and `actions/checkout` fetches a
  single commit with no tags, so the tag existed on the remote and not on the
  runner. It now looks locally first and falls back to `git ls-remote`; a tag
  found in either place passes, a tag found in neither still fails, and an
  unreachable remote warns rather than blocking an offline contributor.

  `v0.9.0` is left in history as what it was. It names a commit whose own CI
  does not pass, for a defect in the release tooling rather than in the
  platform, and `v0.9.1` is the corrected release.

## v0.9.0 - 2026-08-31

### Added

- The architecture documentation is published. Eleven documents that
  existed only on the maintainer's machine are now in the repository: the
  architecture, the 57 architecture decision records, the domain rules, the
  code map, the frontend integration guide, the project definition, the POS
  strategy report, the known issues, both demo documents, and an index. The
  source cites decisions by number 214 times across 31 distinct numbers, and
  none of those citations resolved for anyone but the maintainer.
- The API stability promise is enforced. `scripts/Test-ApiCompatibility.ps1`
  diffs the served OpenAPI document against the accepted baseline in
  `contracts/` and fails on the changes `VERSIONING.md` forbids within a major
  version. CI runs it against a real running instance.
- A populated database is upgraded on every build. The `upgrade` job
  applies the accepted baseline's migrations, seeds through the demonstration
  seed, then applies this build's migrations over the same volume and requires
  readiness, surviving value, and balanced ledger transactions.
- Documentation references are checked. A citation or relative link in a
  tracked file that does not resolve fails the build.
- The portal and cardholder come up with the API.
  `docker compose -f docker-compose.yml -f docker-compose.full.yml up` adds the
  two browser clients, their session databases with split migration and runtime
  roles, and their migration containers. Each client repository gained a
  Dockerfile mirroring the backend's.
- Row-Level Security posture is asserted. `RowLevelSecurityPostureTests`
  checks every table in the twelve module schemas against `pg_class`, so a new
  tenant table cannot be added without either a forced policy or a reviewed
  exemption recorded in `SECURITY.md`.
- A fresh clone is rehearsed. `scripts/Test-FreshCloneRehearsal.ps1` clones
  HEAD and checks what a stranger actually receives, rather than reading a
  working tree that contains untracked files they will never see.
- The demonstration credentials are in `README.md`, with the login and
  organization discovery calls that staff requests depend on.
- Non-Docker local stack start, stop, readiness, and live transaction smoke
  scripts for the backend, portal, cardholder, and POS repositories.
- One-command setup. `cp .env.example .env && docker compose up` brings up
  PostgreSQL, applies every module migration as the migration owner role, and
  starts the API as the runtime role. The API gained a `--migrate` entry point
  that applies all twelve module migrations and exits.
- A Development-only demonstration seed, behind two independent gates, that
  builds a populated tenant by driving the ordinary application services rather
  than writing rows directly. It is idempotent.
- A security policy with a private reporting channel, and a contributor guide.
- Community health files: code of conduct, issue and pull request templates,
  and code owners.

### Changed

- `RELEASE_COMPATIBILITY.json` no longer names tags that do not exist. It
  declared release `v0.5.0-rc.1` and gave all four components that tag, and no
  repository has ever had a public tag. Schema version 2 adds a `development`
  channel for that state, and on a released channel now requires the tag it
  names to resolve locally. The manifest is also byte-identical in all four
  repositories for the first time; it had been CRLF in the backend and LF in
  the other three.
- Branding is no longer promised for 1.0. `VERSIONING.md` said an adopter
  could rebrand through configuration by 1.0. No such configuration was ever
  built, so the promise is withdrawn to an explicit non-goal and noted as
  planned afterwards, rather than carried unbuilt.
- `SECURITY.md` now names the tables that deliberately carry no Row-Level
  Security and why each is correct, instead of claiming RLS as the
  authoritative barrier and leaving the exceptions for a reviewer to find.
- `RELEASE_READINESS.md` recorded 421 integration tests. That was correct when
  written and was not updated when two were added; with three new posture tests
  the total is 426, confirmed by running the suite.
- The API now persists its Data Protection key ring in Development and requires
  an explicit durable shared `DataProtection:KeysPath` in every other
  environment, so queued notification credentials survive restarts and replica
  handoffs.
- The architecture test suite now derives its module list from the assemblies
  in the build output instead of a hand-maintained copy, so a module cannot be
  silently exempt from the boundary and domain-purity rules.
- The demonstration seed issues a second card so organization inventory is not
  empty on first look.
- `.gitattributes` pins shell scripts, Dockerfiles, and YAML to LF.

### Fixed

- The PostgreSQL init script could not run on Linux: a committed UTF-8 byte
  order mark made the interpreter line unreadable.
- The API container could not open a database connection, because the ASP.NET
  runtime image ships no `libgssapi-krb5-2`.
- The container healthcheck probed with a binary the runtime image does not
  ship, so a healthy API was reported unhealthy.
- `Notifications` and `Payments` were absent from the module list and so were
  exempt from every architecture rule. Both were already compliant.
