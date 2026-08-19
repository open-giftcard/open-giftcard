# Contributing

Thanks for looking. This is a small project and the fastest way to get a change
merged is to make it easy to verify.

Open an issue before a large change so we can agree on the shape. Small fixes
can go straight to a pull request.

## Getting a working copy

```bash
cp .env.example .env
docker compose up
```

That brings up PostgreSQL, applies every module migration, and starts the API on
`http://localhost:5143`. Set `DEMO_SEED=true` in `.env` first if you want a
populated tenant to look at.

Docker is not required. The README documents a native PostgreSQL path, which is
what the maintainer uses, and everything below works either way.

## Running the tests

```bash
dotnet test GiftCardPlatform.slnx -c Release
```

Unit and architecture tests need nothing. Integration tests need real
PostgreSQL, because InMemory and SQLite cannot exercise row-level security and
that is precisely what most of them are checking.

They will start their own `postgres:17` container if Docker is available.
Without Docker, point them at a database yourself:

```bash
export GIFTCARD_TEST_CONNECTION="Host=localhost;Port=5432;Database=giftcard_register_test;Username=postgres;Password=..."
dotnet test tests/GiftCardPlatform.IntegrationTests -c Release
```

That connection must be admin-capable: the fixture creates its own roles so the
suite exercises the same privilege separation production uses. It refuses any
database whose name does not contain `test`, and it drops and rebuilds what it
is given.

## What the architecture tests will not let you do

These are enforced, not advisory. If one fails, the design is wrong rather than
the test.

- A module may reference another module's `.Contracts` project and nothing else.
  Implementation types are `internal`.
- One `DbContext` and one PostgreSQL schema per module, with independent
  migrations. A module never touches another module's entities or context.
- Domain code depends on neither EF Core nor ASP.NET Core.
- Every module is covered. `PlatformModules.Names` is compared against the
  assemblies in the build output, so adding a module without listing it fails
  the suite rather than silently exempting it.

One deliberate exception is recorded in `EnforcementCoverageTests`: the
Reporting module composes read-only queries with raw SQL across other modules'
schemas. Reference-based rules cannot see that, so a test pins the property that
makes it acceptable, namely that Reporting owns no `DbContext`.

## Common changes

### Adding an endpoint

Endpoints live in `src/GiftCardPlatform.Api/Endpoints/`, one file per area, and
are mapped from `Program.cs`. Keep them thin: parse, authorize, call the owning
module's application service, map the result. Business behaviour belongs in the
module that owns the concept, so the rule still holds when it is called from a
background worker rather than HTTP.

Never expose an EF Core entity. Return a contract type from the module's
`.Contracts` project.

### Adding a permission

Add the constant to `OrganizationPermissions` or `PlatformPermissions` in
`GiftCardPlatform.Modules.Authorization.Contracts`, and include it in that
class's `All` array. `PermissionCatalogueSynchronizer` inserts anything missing
when the Authorization module migrates, so a new permission reaches existing
databases without a data migration.

Evaluate it in the application service, below the controller. Never branch on a
role name. Scope lives on the membership-role assignment, not on the role, and a
caller cannot grant a permission it does not itself hold.

If existing roles should receive the new permission, that is a backfill
migration; `20260811081300_BackfillPosAndPaymentPlatformPermissions` is the
worked example.

### Adding a migration

```bash
dotnet ef migrations add <Name> \
  --project src/GiftCardPlatform.Modules.<Module> \
  --context <Module>DbContext
```

Migrations run as the migration owner, never the application role. Export
`GIFTCARD_MIGRATIONS_CONNECTION` and apply everything with:

```bash
dotnet run --project src/GiftCardPlatform.Api -- --migrate
```

Two rules that are not negotiable. Every tenant-owned table carries
`organization_id` and a forced RLS policy **in the migration that creates it**;
retrofitting isolation is not an option. And an applied migration is never
edited, because someone else's database has already run it.

### Adding a provider

The seams already exist and are wired in the host rather than inside a module,
so a module never learns where keys or transports come from:

- `INotificationChannelSender` for email and SMS delivery
- `IAuditCheckpointSigner` and `IAuditCheckpointWitness` for audit custody

Implement the interface, register it in `Program.cs`, and the module is
unchanged. `SmtpNotificationSender` and `CapturingNotificationSender` in
`Api/Services/NotificationAdapters.cs` are the pattern to copy.

Please do not add a new interface just to have one. Extract a seam where there
is real volatility, and prefer the existing seam where one fits.

### Changing the API contract

The portal and cardholder each pin a captured copy of the OpenAPI document, and
their CI fails when the recorded hash stops matching the file. A change that
alters the served document means those clients need recapturing at an agreed
backend commit; say so in the pull request.

## What a good pull request looks like

Every behavioural change carries tests. Prioritise integration tests for tenant
isolation, authorization boundaries, database constraints, financial
concurrency, and audit generation, because those are the guarantees the project
actually makes.

For a bug fix, write the failing test first. It is the only way anyone can tell
your fix works, and it is how the layout defect at 200% zoom was finally pinned
down.

The build treats warnings as errors and CI runs the whole suite plus a
`docker compose up` that must reach a healthy API. Run `dotnet test` before
pushing.

Report results honestly. If something is unverified, say so. A pull request that
says "I could not run the browser checks" is more useful than one that implies
they passed.

## Money and tenancy

If your change touches either, read these first. They are invariants, not
preferences.

- Every value change is an immutable, balanced, double-entry ledger posting.
  Debits equal credits per currency.
- No mutable balance column is authoritative. Balances are derived from entries.
- Corrections are compensating transactions. Committed entries are never updated
  or deleted.
- `decimal`, never `float` or `double`, and currency is always explicit.
- Financial operations are idempotent under `operation_type + idempotency_key`
  with a database unique constraint, and run at `SERIALIZABLE`.
- RLS is the authoritative isolation barrier. EF query filters are ergonomics and
  defence in depth, never the only control.
- A client-supplied organization or membership id is never proof of access.
- A refusal must not reveal that another tenant's data exists: 404, not 403.

## Security

Do not open a public issue for a suspected vulnerability. See
[SECURITY.md](SECURITY.md).

## Conduct

The project follows a [code of conduct](CODE_OF_CONDUCT.md). It applies here and
anywhere someone is representing the project.

## Changelog

A change someone using this would notice belongs in
[CHANGELOG.md](CHANGELOG.md) under `Unreleased`, in the pull request that makes
it. Internal refactoring that changes nothing observable does not.

## Licence

Contributions are accepted under the Apache License 2.0, the same terms as the
project.
