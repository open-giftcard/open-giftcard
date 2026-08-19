# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

There is no released version and nothing has been deployed anywhere, so there
are no version headings yet. Everything below has landed on `main` since the
first public commit. The tags that predate the open-source cleanup are not
usable and are not listed.

## Unreleased

### Added

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
