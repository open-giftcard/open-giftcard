# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

There is no released version and nothing has been deployed anywhere, so there
are no version headings yet. Everything below has landed on `main` since the
first public commit. The tags that predate the open-source cleanup are not
usable and are not listed.

## Unreleased

### Added

- **The architecture documentation is published.** Eleven documents that
  existed only on the maintainer's machine are now in the repository: the
  architecture, the 57 architecture decision records, the domain rules, the
  code map, the frontend integration guide, the project definition, the POS
  strategy report, the known issues, both demo documents, and an index. The
  source cites decisions by number 214 times across 31 distinct numbers, and
  none of those citations resolved for anyone but the maintainer.
- **The API stability promise is enforced.** `scripts/Test-ApiCompatibility.ps1`
  diffs the served OpenAPI document against the accepted baseline in
  `contracts/` and fails on the changes `VERSIONING.md` forbids within a major
  version. CI runs it against a real running instance.
- **A populated database is upgraded on every build.** The `upgrade` job
  applies the accepted baseline's migrations, seeds through the demonstration
  seed, then applies this build's migrations over the same volume and requires
  readiness, surviving value, and balanced ledger transactions.
- **Documentation references are checked.** A citation or relative link in a
  tracked file that does not resolve fails the build.
- **The portal and cardholder come up with the API.**
  `docker compose -f docker-compose.yml -f docker-compose.full.yml up` adds the
  two browser clients, their session databases with split migration and runtime
  roles, and their migration containers. Each client repository gained a
  Dockerfile mirroring the backend's.
- **Row-Level Security posture is asserted.** `RowLevelSecurityPostureTests`
  checks every table in the twelve module schemas against `pg_class`, so a new
  tenant table cannot be added without either a forced policy or a reviewed
  exemption recorded in `SECURITY.md`.
- **A fresh clone is rehearsed.** `scripts/Test-FreshCloneRehearsal.ps1` clones
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

- **`RELEASE_COMPATIBILITY.json` no longer names tags that do not exist.** It
  declared release `v0.5.0-rc.1` and gave all four components that tag, and no
  repository has ever had a public tag. Schema version 2 adds a `development`
  channel for that state, and on a released channel now requires the tag it
  names to resolve locally. The manifest is also byte-identical in all four
  repositories for the first time; it had been CRLF in the backend and LF in
  the other three.
- **Branding is no longer promised for 1.0.** `VERSIONING.md` said an adopter
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
