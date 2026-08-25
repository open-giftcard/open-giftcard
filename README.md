# Digital Corporate Gift Card Platform

Secure, multi-tenant platform that digitizes corporate gift cards. Built as a
modular monolith on .NET 10, ASP.NET Core, and PostgreSQL.

Contributor documentation is being rewritten for this release and is not
published yet. Until it lands, this README is the authoritative guide, and the
code is the authority over the README: the architecture tests, the migrations,
and the integration suite describe the system precisely.

## Current state

All four functional phases are implemented. **There is no released version.**
This repository has no tags and no releases, and `main` is the only branch that
receives fixes. Until a release is tagged, the only way to pin a version of this
platform is by commit.

An earlier revision of this section announced a synchronized candidate
`v0.4.0-rc.2` across three repositories, with commit identifiers. Those tags and
commits exist only in the private repositories this project was developed in.
The public repositories were created from a squashed initial commit, so nothing
in that table could be resolved here, and `SECURITY.md` already said the
published tags predate the open-source cleanup and should not be used. The claim
is removed rather than reworded.

Each client repository pins its own capture of the backend OpenAPI document
under `contracts/`, and each `contracts/README.md` records the backend commit
and SHA-256 it was taken from, verified in CI. Those files are the authority for
client-to-backend compatibility.

Nothing here has been deployed anywhere. See "What is not done" below, and the
honest gap list in `SECURITY.md`.

The working gate for the first four-repository public candidate is
[`RELEASE_READINESS.md`](RELEASE_READINESS.md). It records required source,
deployment, operator, recovery, and human evidence without treating a target
version as a completed release.

### What works

**Foundation.** Global identities with email or E.164 phone login, password
hashing, 15-minute JWTs and rotating 30-day refresh tokens with reuse
detection. Customer organization hierarchy to five levels, memberships, and
organization-scoped RBAC where scope lives on the role *assignment*, so one role
is reusable at different scopes. Platform authority is a separate model
from customer membership. PostgreSQL Row-Level Security is the authoritative
isolation barrier, not application filtering.

**Money.** A posted-only balanced double-entry Ledger is the sole financial
authority. Corporate credit allocation and reversal, gift-card issuance from a
funding root into organization inventory, distribution to an email address or
phone number that has no account yet, single-use claim and activation, bounded
all-or-nothing bulk batches of up to 100, and lifecycle suspend, reactivate,
cancel, and expire with exact value return.

**Sharing.** A cardholder can split a card. Creating a share reserves its amount
immediately; the Ledger transfer happens only on successful claim. Protected
links carry a 256-bit secret plus a six-digit PIN, expire in 24 hours, are
single-use, and lock permanently after five wrong attempts. Contact-bound
invitations reuse the verified activation path so a link plus a PIN is never
mistaken for identity verification.

**Payments.** A 60-second single-use credential presented as either an opaque QR
code or a 12-digit numeric code. POS clients and terminals authenticate
separately from the cardholder. A sale takes a two-minute provision that reserves
value without posting anything, then confirms for any amount up to that ceiling,
releasing the remainder. Refunds are immutable and cumulatively capped.
Reporting exposes gross, refunded, and net across stores, terminals, receipts,
and reversals.

**Audit.** Append-only records the runtime role cannot update or delete, plus
signed tamper-evident checkpoints: batches are sealed behind a database sequence,
canonicalised, reduced to a SHA-256 Merkle root, chained, and signed. Audit
writers stay concurrent, and a signer outage delays sealing without ever
refusing a purchase.

**Notification delivery.** Activation messages are queued in a transactional
outbox *inside* the business transaction, so a message becomes durable exactly
when the distribution does. A dispatcher delivers with bounded retry, backoff,
and dead-lettering. An SMTP sender ships for demonstrations.

### What is not done

This is not deployment-certified, and the remaining gaps are explicit:

- **Managed audit custody.** The `RemoteHttp` provider signs and publishes
  through a separately operated custody gateway over mutual TLS, so the
  checkpoint private key stays in KMS/HSM custody and this application never
  holds it. It has only run against a stubbed transport. No gateway, WORM
  retention policy, or verification drill has run in a named environment, and
  the mutual TLS handshake itself is unproven. `DevelopmentFile` is still
  refused outside Development.
- **SMS.** No provider adapter. Outside Development, phone distribution, direct
  sharing, and bulk acceptance fail with `notification.channel.unconfigured`
  before business state is committed.
- **Deployment and operations.** Coordinated versioned non-Docker archives,
  explicit migrators, SBOMs, checksums, recovery and rollback drills, and an
  automated staging evidence gate exist. No named environment has yet supplied
  certified TLS, DNS, ingress, central logging, central metrics, or recovery
  evidence.
- **Staging certification.** Never deployed anywhere.
- **POS counter application.** The `open-giftcard-pos` repository holds a
  demonstration till against the pinned backend payment contract. It is not
  retail software: the basket is a fixed mock. The automated live-backend smoke
  journey covers readiness, device authentication, payment, reporting, and full
  refund; physical counter-device and human browser certification remain.

Deployment gates are summarised under *What is not done* above. See the tracked
[deployment guide](docs/DEPLOYMENT.md) for the native archive, migration,
staging evidence, recovery, and rollback procedures.

### Client boundary

The portal and cardholder applications are separate repositories that consume
the versioned OpenAPI contract. Browser deployments use a same-origin BFF that
keeps refresh tokens server-side; the API stays bearer-only and does not enable
broad CORS. See
the portal and cardholder repositories, each of which pins this backend
contract under `contracts/`.

## Quick start with Docker

```bash
cp .env.example .env
docker compose up
```

Compose brings up three services in order. PostgreSQL creates the two
application roles and the twelve schemas from `infra/postgres/init`. A
migrations container then applies every module's schema as the migration owner
and exits. Only then does the API start, as the runtime role, which owns nothing
and cannot alter its own schema (ADR-019). Compose also mounts the
`giftcard-dataprotection` named volume at the API's explicit key path. The
non-root runtime user owns that path, so queued notification credentials remain
decryptable across container replacement without granting root privileges.

The API is published on `http://localhost:5143`; `GET /health/ready` reports
when it is serving. Set `DEMO_SEED=true` in `.env` to build a demonstration
tenant on first start: an organization with two child organizations, a company
administrator, corporate credit, an issued and claimed card, a confirmed payment,
and a partial refund. That seed is Development-only and is not registered in any
other environment.

Docker is not required. The native PostgreSQL path below is fully supported and
is what the maintainer uses; the sections after it apply either way.

## Requirements

- .NET 10 SDK
- PostgreSQL 18, running locally
- PostgreSQL's `psql` command-line client
- `dotnet-ef` tooling: `dotnet tool install --global dotnet-ef`

---

## Local demonstration

All commands run from the repository root. PowerShell syntax; on bash replace
`$env:NAME = "value"` with `export NAME="value"`.

### 1. Configure local credentials

```powershell
Copy-Item .env.example .env
```

Edit `.env` and replace every `change_me_locally` value. `.env` is git-ignored
and must never be committed.

### 2. Prepare PostgreSQL

Make sure the PostgreSQL 18 Windows service is running, then connect as the
local PostgreSQL administrator:

```powershell
$env:PGPASSWORD = "<your PostgreSQL administrator password>"
psql -h localhost -p 5432 -U postgres -d postgres
```

If `psql` is not on `PATH`, use
`& "C:\Program Files\PostgreSQL\18\bin\psql.exe"` in its place.

At the `psql` prompt, run the following once on a new local installation.
Replace both password placeholders with the corresponding values from `.env`:

```sql
CREATE ROLE giftcard_migrator
    LOGIN PASSWORD '<your migrator password>'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

CREATE ROLE giftcard_app
    LOGIN PASSWORD '<your app password>'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

CREATE DATABASE giftcard OWNER giftcard_migrator;
\connect giftcard

GRANT CONNECT ON DATABASE giftcard TO giftcard_migrator, giftcard_app;

CREATE EXTENSION IF NOT EXISTS ltree;

CREATE SCHEMA IF NOT EXISTS organizations AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS audit AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS identity AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS "authorization" AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS ledger AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS corporate_credits AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS gift_cards AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS distribution AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS sharing AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS payments AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS notifications AUTHORIZATION giftcard_migrator;
CREATE SCHEMA IF NOT EXISTS partners AUTHORIZATION giftcard_migrator;

GRANT USAGE ON SCHEMA organizations, audit, identity, "authorization",
    ledger, corporate_credits, gift_cards, distribution, sharing, payments,
    notifications, partners
    TO giftcard_app;
REVOKE CREATE ON SCHEMA organizations, audit, identity, "authorization",
    ledger, corporate_credits, gift_cards, distribution, sharing, payments,
    notifications, partners
    FROM giftcard_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA organizations
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA "authorization"
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA identity
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA audit
    GRANT SELECT, INSERT ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA ledger
    GRANT SELECT, INSERT ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA corporate_credits
    GRANT SELECT, INSERT ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA gift_cards
    GRANT SELECT, INSERT, UPDATE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA distribution
    GRANT SELECT, INSERT, UPDATE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA sharing
    GRANT SELECT, INSERT, UPDATE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA payments
    GRANT SELECT, INSERT, UPDATE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA notifications
    GRANT SELECT, INSERT, UPDATE ON TABLES TO giftcard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE giftcard_migrator IN SCHEMA partners
    GRANT SELECT, INSERT, UPDATE ON TABLES TO giftcard_app;

\quit
```

Back in PowerShell, clear the temporary administrator password:

```powershell
Remove-Item Env:\PGPASSWORD
```

These are the two database roles required by ADR-019:

| Role                | Purpose                                     | Privileges                                            |
| ------------------- | ------------------------------------------- | ----------------------------------------------------- |
| `giftcard_migrator` | Owns schemas, tables, and migrations        | DDL; not used at runtime                              |
| `giftcard_app`      | Used by the running API                     | Non-superuser, `NOBYPASSRLS`, DML only                |

The runtime role holds **no UPDATE or DELETE privilege on `audit.audit_records`**,
which is what enforces the append-only guarantee at the database level.

### 3. Apply migrations

Migrations run as the migration owner, never as the application role.

The API image applies all twelve in one step, which is what the compose
migrations service runs:

```bash
GIFTCARD_MIGRATIONS_CONNECTION="Host=localhost;Port=5432;Database=giftcard;Username=giftcard_migrator;Password=<your migrator password>"   dotnet run --project src/GiftCardPlatform.Api -- --migrate
```

It refuses to run without that variable rather than connecting as the
application role, which silently does nothing for already-applied modules and
then fails on the first new one. The twelve individual commands remain valid and
are listed here because they are useful when migrating one module at a time:

```powershell
$env:GIFTCARD_MIGRATIONS_CONNECTION = "Host=localhost;Port=5432;Database=giftcard;Username=giftcard_migrator;Password=<your migrator password>"

dotnet ef database update --project src/GiftCardPlatform.Modules.Organizations --context OrganizationsDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Audit --context AuditDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Authorization --context AuthorizationDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Identity --context IdentityDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Ledger --context LedgerDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.CorporateCredits --context CorporateCreditsDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.GiftCards --context GiftCardsDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Distribution --context DistributionDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Sharing --context SharingDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Payments --context PaymentsDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Notifications --context NotificationsDbContext
dotnet ef database update --project src/GiftCardPlatform.Modules.Partners --context PartnersDbContext
```

Twelve modules each own a schema and an independent migration history:
`organizations`, `audit`, `authorization`, `identity`, `ledger`,
`corporate_credits`, `gift_cards`, `distribution`, `sharing`, `payments`,
`notifications`, and `partners`.

### 4. Configure email delivery (optional)

Skip this and Development captures activation links in the outbox instead of
sending them; the demo console can still open them. Configure it and real email
goes out, which is what makes the activation journey convincing to an audience.

The non-secret half lives in `appsettings.Development.json` under
`Notifications:Smtp`. For Gmail, `Host` is `smtp.gmail.com` and `Port` is `587`;
set `FromAddress` to the mailbox the credential belongs to.

`Enabled` is committed as `false` on purpose. When it is on, the API refuses to
start without a password, so a `true` in a committed file would break a fresh
clone and CI for anyone who has not set the secret. Both the switch and the
password are therefore per-machine, and both live in user secrets:

```powershell
dotnet user-secrets set --project src/GiftCardPlatform.Api "Notifications:Smtp:Enabled" "true"
dotnet user-secrets set --project src/GiftCardPlatform.Api "Notifications:Smtp:Password" "<app password>"
```

Gmail requires an *app password*, not the account password: enable 2-Step
Verification, then create one at
[myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords).
Outlook.com personal accounts no longer support password-based SMTP and will
not work with this adapter.

For a demonstration, sending to a plus-alias such as
`you+demo1@gmail.com` lets you distribute several cards and claim each one
without extra mailboxes.

### 5. Run the API

```powershell
$env:ConnectionStrings__Default = "Host=localhost;Port=5432;Database=giftcard;Username=giftcard_app;Password=<your app password>"
$env:Authentication__Jwt__SigningKey = "<random development value of at least 32 bytes>"
$env:Bootstrap__PlatformAdministrator__Secret = "<different random value of at least 32 bytes>"
$env:Partners__EpinDeliveryKey = "<Base64-encoded random 32-byte value>"
$env:Partners__ClaimBaseUrl = "http://localhost:5180/epin"
$env:Partners__MintRateLimit__PermitLimit = "60"
$env:Partners__MintRateLimit__WindowSeconds = "60"
$env:ASPNETCORE_ENVIRONMENT = "Development"

dotnet run --project src/GiftCardPlatform.Api
```

### 6. Open Swagger or the demo UI

```text
http://localhost:5xxx/swagger
http://localhost:5xxx/demo
```

### Local reseller e-pin certification without deployment

When a local PostgreSQL server is installed, the complete reseller backend and
cardholder regression gates can run without Docker or a deployed environment:

```powershell
.\scripts\Test-PartnerEpinLocal.ps1
```

The script securely prompts for the local PostgreSQL administrator password,
creates only the guarded disposable `giftcard_partner_epin_test` database, runs the
Release build, unit and architecture suites, the real-PostgreSQL partner tests,
and the sibling cardholder repository's full suite, then removes the connection
string and password from its process environment. It never targets the normal
development database. Use `-AllIntegrationTests` to run the entire PostgreSQL
integration suite rather than only PARTNER-001.

Every integration run writes a fresh ignored TRX under
`tests/GiftCardPlatform.IntegrationTests/TestResults` and prints failed test
names and assertions before returning an error. After a failed full run, repeat
only that database gate with:

```powershell
.\scripts\Test-PartnerEpinLocal.ps1 -IntegrationOnly -AllIntegrationTests
```

### Run the complete local stack without Docker

The repository includes a Windows PowerShell runner for the sibling backend,
portal, cardholder, and POS repositories. PostgreSQL still runs as an ordinary
local service. Prepare each application's database once as described in its
README, then provide the two browser-session connection strings through the
environment or ignored `.env` files:

```powershell
$env:ConnectionStrings__Portal = "Host=localhost;Port=5432;Database=giftcard_portal;Username=giftcard_portal_app;Password=<local password>"
$env:ConnectionStrings__PortalMigrations = "Host=localhost;Port=5432;Database=giftcard_portal;Username=giftcard_portal_migrator;Password=<local migration password>"
$env:ConnectionStrings__Cardholder = "Host=localhost;Port=5432;Database=giftcard_cardholder;Username=giftcard_cardholder_app;Password=<local password>"
$env:ConnectionStrings__CardholderMigrations = "Host=localhost;Port=5432;Database=giftcard_cardholder;Username=giftcard_cardholder_migrator;Password=<local migration password>"

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Start-OpenGiftCardLocal.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-OpenGiftCardSmoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Stop-OpenGiftCardLocal.ps1
```

The start command applies pending backend, portal, and cardholder migrations,
keeps per-application Data Protection keys and logs under ignored
`.local/stack`, waits for all five HTTP processes, and records only processes
it started. For an old local setup without split client roles, it can use the
runtime connection as the local migration owner and prints a warning. It
refuses occupied ports by default. Pass `-UseExisting` to verify healthy
services that are already running; the stop command will leave those processes
alone.

The smoke command checks the runtime PostgreSQL role and forced RLS, signs in
through real API endpoints, reads the seeded tenant and recipient card, creates
a fresh POS device, performs a balance inquiry, holds and confirms one unit,
reads the platform receipt, and refunds the full amount. Secrets and payment
credentials are never written to the console or the process-state file. The
smoke till secret is reused from an ignored file encrypted to the current
Windows user through DPAPI.

For a named staging environment, use the same gate with deployment URLs, the
clean artifact manifest that was deployed, and pre-provisioned smoke identities
and POS credentials supplied through the process environment:

```powershell
$env:OPEN_GIFTCARD_SMOKE_PLATFORM_EMAIL = '<staging platform operator>'
$env:OPEN_GIFTCARD_SMOKE_RECIPIENT_EMAIL = '<staging recipient>'
$env:OPEN_GIFTCARD_SMOKE_PASSWORD = '<staging smoke password>'
$env:OPEN_GIFTCARD_SMOKE_POS_CLIENT_CODE = '<staging POS client>'
$env:OPEN_GIFTCARD_SMOKE_POS_TERMINAL_CODE = '<staging terminal>'
$env:OPEN_GIFTCARD_SMOKE_POS_CLIENT_SECRET = '<staging POS secret>'
$env:ConnectionStrings__Default = '<staging backend runtime connection>'

.\scripts\Test-OpenGiftCardSmoke.ps1 `
  -EnvironmentName 'staging-rc1' `
  -BackendBaseUrl 'https://api.staging.example' `
  -PortalUrl 'https://portal.staging.example' `
  -PortalBffBaseUrl 'https://portal.staging.example' `
  -CardholderBaseUrl 'https://card.staging.example' `
  -PosBaseUrl 'https://pos.staging.example' `
  -ArtifactManifestPath '<downloaded-artifact-set>\ARTIFACTS.json' `
  -EvidencePath '.local\certification-evidence\staging-rc1-smoke.json'
```

Outside the `local` environment the gate requires HTTPS, a non-rehearsal
four-application artifact set with matching ZIP, SBOM, embedded contract, and
build metadata hashes, a least-privilege backend runtime connection, non-default
credentials, and a pre-provisioned POS identity. It never provisions or stores a
staging POS secret. The evidence JSON contains exact artifact and commit hashes,
readiness, forced-RLS, and transaction results without credentials, and is
paired with its own SHA-256 file. Existing evidence is never overwritten.
`-AllowInsecureHttp` exists only for a non-certifying network rehearsal and
records that the run does not count as deployment-verified smoke evidence.

After the automated staging smoke passes, copy
`STAGING_ACCEPTANCE.example.json` into the private evidence workspace. Replace
every `not-run` result with the human or operator result, owner, and a
non-secret evidence reference. Set the real review time and decision, then bind
that review to the smoke record:

```powershell
.\scripts\New-StagingAcceptanceRecord.ps1 `
  -AutomatedSmokeEvidencePath '.local\certification-evidence\staging-rc1-smoke.json' `
  -ReviewPath '<private-evidence-workspace>\staging-rc1-review.json' `
  -OutputPath '<new-evidence-directory>\staging-rc1-acceptance.json'
```

The recorder verifies the smoke checksum, named HTTPS environment, exact
release identity, review timing, and all 17 required product, accessibility,
recovery, and operations checks. Approval is refused when a check is failed or
not run, a blocking issue remains, or the review asks to approve a different
environment. The checksum-protected result records evidence references and
owners, not credentials or secret values.

The backend also has an opt-in stable OTLP HTTP/protobuf metrics path and six
release-critical Prometheus-compatible alerts for missing telemetry,
readiness, 5xx rate, p95 latency, repeated worker failure, and audit
verification failure. The operator contract and alert rules are under
`monitoring/`. After the smoke gate has generated traffic on every replica, run
`scripts/Test-OpenGiftCardObservability.ps1` against the named metrics API. It
binds a redacted checksum-protected observability record to the deployed
`ARTIFACTS.json`; details are in `docs/DEPLOYMENT.md`.

Use the port printed by `dotnet run`. `/demo` is the responsive development console:
it guides bootstrap/login, customer onboarding, initial Company Administrator
assignment, hierarchy, memberships, roles, corporate-credit allocation,
ledger-derived balance/history viewing, organization card issuance/inventory,
individual and max-100 bulk email/phone distribution, batch result lookup,
local activation delivery, recipient claim, organization/platform lifecycle
control, returned-value history, cardholder suspend/reactivate,
organization financial summaries/unified history/read-only reconciliation,
recipient-owned card balances/history, email-or-phone login, and session
rotation/revoke. It also demonstrates protected-share creation, the one-time
link/PIN response, authenticated claim, direct email/phone invitation and both
activation branches, sent/received history, cancellation, and
posted/reserved/available value. It is
available only in Development, holds no business rules, and uses only public
`/api/v1` endpoints.

Bulk creation uses
`POST /api/v1/organizations/{organizationId}/gift-card-batches`; durable
results can be retrieved with
`GET /api/v1/organizations/{organizationId}/gift-card-batches/{batchId}`.
Enter one email address or E.164 phone number per line in the demo. The first
version accepts at most 100 recipients and deliberately provides no partial
success.

The Development activation link defaults to
`http://localhost:5143/demo#/activation`. If the API is started on another
port, set `Distribution__ClaimBaseUrl` to that origin plus
`/demo#/activation`. Claim tokens default to 24 hours, lock after five invalid
secret attempts, and the endpoint defaults to 10 requests per source IP per
minute; all three values are configurable under `Distribution`.

Protected share links default to `http://localhost:5180/share/claim`, expire
after exactly 24 hours, and permanently lock after five failed PIN attempts.
Set `Sharing__ClaimBaseUrl` to the cardholder BFF route for the target
environment. The bounded expiry worker is enabled by default and is configured
with `Sharing__ExpirationEnabled`, `Sharing__ExpirationPollIntervalSeconds`,
and `Sharing__ExpirationBatchSize`.

Payment QR issuance, provision creation, and confirmation default to 60
requests per minute under `Payments:RedemptionRateLimit:PermitLimit`. The quota
is partitioned by authenticated user or POS client, so tills behind the same
store gateway do not collapse into one source-IP bucket.

Audit checkpoint defaults are
`Audit__Checkpoints__Enabled=false`,
`Audit__Checkpoints__Provider=DevelopmentFile`,
`Audit__Checkpoints__PollIntervalSeconds=300`, and
`Audit__Checkpoints__BatchSize=10000`. Enabling checkpointing requires naming
the provider; an unrecognised value fails startup rather than leaving
checkpointing off.

`DevelopmentFile` runs only in Development and requires explicit
`DevelopmentSigningKeyPath` (an ECDSA P-256 PEM private key) and
`DevelopmentWitnessDirectory`. The local witness uses create-only files and is
for developer verification only; it is not a production WORM substitute.

`RemoteHttp` calls a custody gateway and takes
`RemoteSignerEndpoint`, `RemoteSignerKeyId`, `RemoteWitnessBaseUrl`, and
`RemoteTimeoutSeconds` (default 30). Client authentication needs exactly one of
`RemoteClientCertificatePath` with `RemoteClientCertificatePassword`, or
`RemoteClientCertificateThumbprint` for the Windows certificate store. Both
URLs must be absolute HTTPS without credentials, query, or fragment.

Platform payment reporting is available at
`GET /api/v1/platform/reports/payments` and
`GET /api/v1/platform/reports/payments/{paymentProvisionId}`. It requires
`platform.payments.view`, supports exact store/client/terminal/tenant/state/
currency filters, literal receipt/card/POS reference search, inclusive/exclusive
UTC bounds, and filter-bound cursor pagination. Page and all-matching totals
preserve currency boundaries and expose provisioned, confirmed, refunded, net,
refund-count, and fully reversed values. Receipt detail returns ordered refund
lines and immutable Ledger links without owner identity, payment credentials,
POS secrets, or idempotency keys.

Direct share activation links default to
`http://localhost:5180/activate/share`. Set `Sharing__DirectClaimBaseUrl` to the
cardholder BFF clean-intake route. The Development console captures the
durable outbox delivery for local proof. SMTP is the reference email adapter.
Without a sender for the requested channel, new work is refused before commit;
a real SMS provider remains required before phone delivery can be enabled in a
staging or pilot environment.

Behind a BFF or reverse proxy, configure each immediate trusted proxy as a
literal IP address under `Networking:ForwardedHeaders:KnownProxies`, for example
`Networking__ForwardedHeaders__KnownProxies__0=127.0.0.1`. The backend then
honors one `X-Forwarded-For` address before applying source-IP rate limits. The
proxy must overwrite that header from its observed client connection and must
not append or relay a browser-supplied value. With no allowlist, forwarded
addresses are ignored and the direct remote address remains authoritative.

Partner mint velocity is enforced separately by a PostgreSQL fixed window keyed
to the authenticated partner client. `Partners__MintRateLimit__PermitLimit` and
`Partners__MintRateLimit__WindowSeconds` configure the shared budget. Database
time, an atomic upsert, and forced RLS keep the cap exact across API replicas and
hide each client's counter from every other execution context. Keep an
unauthenticated source-IP limit at the public ingress as a coarse flood control;
it does not replace the backend's authenticated quota.

Run a database and key-ring recovery drill without Docker using
`scripts/Test-PostgresRecovery.ps1`. It reads the source database, creates a
separate database whose name must start with `giftcard_recovery_test_`, restores
the custom-format backup, compares catalog ownership, RLS and forced-RLS flags,
row counts, and sequence state, then removes only that guarded restore target.
Pass every deployed Data Protection key-ring directory through `-KeyRingPath`;
the drill copies each ring through backup and restore locations and verifies a
SHA-256 manifest. Use `-KeepRestoreDatabase` or `-KeepArtifacts` only when an
operator deliberately needs the isolated evidence for investigation.

After applying the candidate migrations to that isolated restore, verify an
application rollback with `scripts/Test-BackendRollback.ps1`. Supply the current
and previous published artifact directories, the guarded restore database's
runtime connection, and the restored backend key ring. The probe starts each
artifact in Production mode on separate loopback ports and requires
`/health/ready` from both. Readiness proves schema and key compatibility only.
Before routing traffic back to an artifact that predates the shared partner mint
quota or notification-channel guard, enforce equivalent ingress controls or
disable the affected routes; never trade a deployment incident for a known
security regression.

Build the coordinated non-Docker release bundle from this repository after all
four sibling repositories carry the same `RELEASE_COMPATIBILITY.json`:

```powershell
.\scripts\Build-ReleaseArtifacts.ps1 `
  -SbomToolPath .\.local\tools\sbom-tool-v4.1.5.exe `
  -NoRestore
```

Use Microsoft SBOM Tool v4.1.5. The builder verifies its published SHA-256
`625767B371B7FDD58F40F618B8A86DA0247A33C89E419039C86B4EDBA1DAD4B5`
before execution. The command refuses dirty repositories for a real build. Use
`-AllowDirty` only for a local rehearsal. It publishes the backend, portal BFF
plus compiled web client, cardholder, and POS into four versioned ZIP files
under `.local/release-artifacts`. Every ZIP embeds a validated SPDX 2.2 SBOM;
matching sidecars, `BUILD_INFO.json`, `ARTIFACTS.json`, and `SHA256SUMS` make the
bundle independently inspectable. The builder also rejects missing entry
points, mismatched release contracts, path traversal, and common secret or key
file names. An artifact rehearsal is not a public release: final commit pins,
CI provenance, staging evidence, and tags are separate gates.

Automatic expiration is enabled by default, polls every 30 seconds, and
processes at most 50 due cards per batch. Configure it under
`GiftCards:Expiration`, or with
`GiftCards__Expiration__Enabled`,
`GiftCards__Expiration__PollIntervalSeconds`, and
`GiftCards__Expiration__BatchSize`. The interval must be 5–86400 seconds and
the batch size 1–100. Each card is PostgreSQL-serialized and idempotent, so a
retry cannot duplicate the terminal value return.

### 7. Authenticate

Call the one-time bootstrap exactly once:

```powershell
curl.exe -X POST http://localhost:5000/api/v1/bootstrap/platform-administrator `
  -H "Content-Type: application/json" `
  -H "X-Platform-Bootstrap-Secret: <the configured bootstrap secret>" `
  -d '{\"email\":\"platform.admin@example.com\",\"password\":\"a long development passphrase\"}'
```

Then call `POST /api/v1/auth/login` with that email and password. Recipient
accounts use either the email or `phoneNumber` selected during activation, never
both. Login returns a 15-minute bearer JWT and a one-time 30-day refresh token
without sending another email or SMS. Rotate it through
`POST /api/v1/auth/refresh`; revoke its session through
`POST /api/v1/auth/revoke`. Remove the bootstrap secret from runtime
configuration after the first success; PostgreSQL permanently refuses any
second bootstrap.

JWT bearer authentication is the only identity mechanism in every environment.
A bearer caller selects a customer organization with `X-Organization-Id`; the
API verifies an active membership before accepting that scope. Caller-selected
development identity and permission headers are not supported.

### 8. Create an organization

```powershell
curl.exe -X POST http://localhost:5000/api/v1/organizations `
  -H "Content-Type: application/json" `
  -H "Authorization: Bearer <platform administrator access token>" `
  -d '{\"name\":\"Example Customer Company\",\"code\":\"EXAMPLE\"}'
```

Returns `201 Created`:

```json
{
  "id": "0192f4c2-...",
  "name": "Example Customer Company",
  "code": "EXAMPLE",
  "status": "Active",
  "depth": 0,
  "createdAtUtc": "2026-07-23T10:30:00+00:00"
}
```

### 9. Read it back

```powershell
curl.exe http://localhost:5000/api/v1/organizations/<id> `
  -H "Authorization: Bearer <platform administrator access token>"
```

### 10. Inspect both rows in PostgreSQL

```powershell
$env:PGPASSWORD = "<your app password>"

psql -h localhost -p 5432 -U giftcard_app -d giftcard -c "select id, name, code, status, depth, parent_organization_id, hierarchy_path::text from organizations.organizations;"

psql -h localhost -p 5432 -U giftcard_app -d giftcard -c "select operation, entity_type, entity_id, outcome, actor_type, occurred_at_utc from audit.audit_records order by occurred_at_utc desc limit 5;"
```

Confirm the append-only guarantee — this must fail with `permission denied`:

```powershell
psql -h localhost -p 5432 -U giftcard_app -d giftcard -c "delete from audit.audit_records;"
Remove-Item Env:\PGPASSWORD
```

---

---

## Full end-to-end demonstration

The single-repository walkthrough above proves the backend. This section runs
the complete business journey across all three applications, the way a customer
would actually experience it: the platform operator funds a company, the company issues and sends
a card, a recipient activates it by email, shares part of it, and finally pays
at a till.

Everything runs natively on Windows. Docker is not required.

### Additional requirements

- Node.js 24 or newer and pnpm 11, for the portal front end
- All three repositories cloned as siblings:

```text
GitHub/
  open-giftcard              backend, this repository
  open-giftcard-portal       staff and platform operator portal
  open-giftcard-cardholder   recipient application
  open-giftcard-pos          reference counter till
```

Leave all four on compatible revisions. They must be
consistent: the clients are pinned to a specific backend contract and will
refuse a mismatched revision, and CI fails a client whose recorded contract hash
does not match the document beside it.

### The moving parts

| What | Port | Started by |
| --- | --- | --- |
| Backend API | 5143 | `dotnet run --project src/GiftCardPlatform.Api` |
| Backend demo console | 5143 `/demo` | served by the API in Development |
| Cardholder app | 5180 | `dotnet run --project src/GiftCardCardholder.Web` |
| Portal development web | 5173 | `pnpm run dev` in `GiftCardPortal.Web` |
| Portal BFF and bundled web | 5179 | `dotnet run --project src/GiftCardPortal.Bff` |
| POS till | 5190 | `dotnet run --project src/GiftCardPos.Web` |

Ports come from each repository's launch profile. The backend prints its port on
startup; if it differs, adjust the client `ClaimBaseUrl` settings to match.

### Before you start

Do these once, or the journey stalls half way and it is not obvious why:

1. **Enable SMTP** (step 4 above). Without it, activation links are queued but
   never sent, and you have to read them out of the demo console instead of an
   inbox. That still works, but it is far less convincing to watch.
2. **Keep the Data Protection key ring.** Native Development stores it under
   `.local/dataprotection-keys`, which is ignored by Git; Compose uses its
   `giftcard-dataprotection` named volume. Deleting either store makes previously
   queued notification payloads unreadable. Non-Development startup requires an
   explicit durable `DataProtection:KeysPath` shared by all API instances.
3. **Have two browser profiles or a private window ready.** The staff session and
   the recipient session are different identities; sharing one browser means
   logging in and out repeatedly in front of an audience.

### The journey

**1. Bootstrap the platform.** In `/demo`, use the one-time bootstrap to create
the first platform administrator. It works exactly once; the durable
completion row makes every later attempt fail closed.

**2. Onboard a customer.** As the platform administrator, create a customer
organization and assign its first Company Administrator. That assignment is the
only cross-tenant write in the system and is audited as such.

**3. Fund it.** Allocate corporate credit to the customer. Watch the Ledger: the
allocation moves value from the platform funding account to the customer's
corporate-credit account. Nothing anywhere edits a balance column.

**4. Issue and send a card.** As the Company Administrator, issue a gift card
from inventory, then distribute it to an email address you can open. Use a
plus-alias such as `you+demo1@gmail.com`.

The activation email is queued **inside** the distribution transaction and sent
by the dispatcher within about five seconds. The API console logs:

```text
Notification dispatch delivered 1, retrying 0, dead-lettered 0
```

If you skipped SMTP, open the link from the demo console's delivery lookup
instead.

**5. Activate as the recipient.** Open the link from the inbox. It creates the
recipient identity and transfers ownership of the card. The link is single-use:
opening it a second time does nothing. Receiving a card grants no organization
membership, which is worth pointing out.

**6. Share part of the balance.** In the cardholder app, split the card. The
share reserves its amount immediately and the card's *available* balance drops
while its *posted* balance does not. The Ledger transfer happens only when the
recipient claims, using the 256-bit link plus the six-digit PIN.

**7. Pay at a till.** In the cardholder app, request a payment credential. The
same 60-second single-use record is shown as both a QR code and a 12-digit
numeric code.

Open the reference till at `http://localhost:5190`, configure its client and
terminal credentials, and submit the displayed QR or numeric code. Integrators
can also drive the same contract through Swagger or `curl`:

```text
POST /api/v1/pos/clients                        register a POS client   (platform)
POST /api/v1/pos/clients/{id}/disable           retire a POS client     (platform)
POST /api/v1/pos/clients/{id}/terminals         register a terminal     (platform)
POST /api/v1/pos/clients/{id}/terminals/{terminalId}/disable  retire one terminal
POST /api/v1/pos/auth/token                     exchange for a device token
POST /api/v1/pos/payment-provisions             present the credential, hold value
POST /api/v1/pos/payment-provisions/{id}/confirm  charge up to the held amount
```

Client and terminal retirement is permanent, permission-protected, and audited
once. The API re-resolves device state from PostgreSQL on every authenticated
POS request, so both new and already-issued device tokens are refused as soon as
their client or terminal is disabled. Rotate a client secret by registering a
replacement client and terminals, moving lanes to the new one-time secret, and
then disabling the old client; the backend never stores a recoverable secret.

Between the provision and the confirmation, refresh the cardholder view: the
held amount is reserved and unspendable, but nothing has been posted to the
Ledger yet. That gap is the whole point of a provision, and it demonstrates well.

**8. Show the money.** In the portal, open the POS payment report as a platform
operator: stores, terminals, receipts, and gross, refunded, and net totals per
currency. Then open the organization's financial history and reconciliation. The
reconciliation is read-only by design and never repairs anything it finds.

### If something does not work

| Symptom | Cause |
| --- | --- |
| No email, log says `retrying` | Transient SMTP failure. A wrong app password looks like this and retries eight times over roughly an hour. |
| No email, log says `dead-lettered` | Permanent. Usually a rejected recipient address, or an expired credential. |
| No email, no log line at all | The dispatcher is disabled, or nothing was queued because the distribution rolled back. |
| Client refuses to start | Backend revision does not match the pinned contract. Check `contracts/README.md` in that client. |

Inspect the queue directly:

```sql
select kind, channel, masked_recipient, state, attempt_count, last_failure_code
from notifications.outbox_messages
order by created_at_utc desc
limit 10;
```

A delivered or dead-lettered row has no body: the credential is destroyed on
every terminal transition, so an activation link exists at rest only while it is
still needed.

## Tests

```powershell
# Fast suites — no database required
dotnet test tests/GiftCardPlatform.UnitTests
dotnet test tests/GiftCardPlatform.ArchitectureTests
```

The integration suite always runs against real PostgreSQL — never EF InMemory or
SQLite, which cannot enforce RLS, check constraints, unique indexes, or `ltree`
columns.

In PostgreSQL 18, create a **dedicated, disposable
test database** — its module schemas are dropped and rebuilt
on every run. Two guardrails are enforced: the database name must contain `test`,
and the harness records a marker table in it.

```powershell
$env:PGPASSWORD = "<your PostgreSQL administrator password>"
psql -h localhost -p 5432 -U postgres -d postgres -c "CREATE DATABASE giftcard_test;"
Remove-Item Env:\PGPASSWORD

# Keep this set while running integration tests. The connection must be
# admin-capable because the harness creates roles and schemas.
$env:GIFTCARD_TEST_CONNECTION = "Host=localhost;Port=5432;Database=giftcard_test;Username=postgres;Password=<your PostgreSQL administrator password>"

dotnet test tests/GiftCardPlatform.IntegrationTests
```

The suite provisions the two roles from ADR-019, applies the real migrations as
the migration owner, and issues API requests through the runtime application
role. Test passwords are generated per run and never logged. If `giftcard_test`
already exists, skip the database-creation command.

### Everything

```powershell
# GIFTCARD_TEST_CONNECTION must still be set for the integration suite.
dotnet test

Remove-Item Env:\GIFTCARD_TEST_CONNECTION
```

## Solution layout

```text
src/
  GiftCardPlatform.Api                        ASP.NET Core host, JWT endpoints, development console, background workers
  GiftCardPlatform.BuildingBlocks             execution context, transaction coordinator, session context
  GiftCardPlatform.Modules.Identity           users, password login, JWT sessions, refresh rotation
  GiftCardPlatform.Modules.Organizations      organization domain, application, persistence
  GiftCardPlatform.Modules.Authorization      roles, permissions, scoped assignments, evaluator
  GiftCardPlatform.Modules.Audit              append-only audit records
  GiftCardPlatform.Modules.Ledger             immutable accounts, transactions, entries, balances
  GiftCardPlatform.Modules.CorporateCredits   allocations, reversals, balance/history queries
  GiftCardPlatform.Modules.GiftCards          funded issuance, inventory, lifecycle/expiration, ownership/provenance
  GiftCardPlatform.Modules.Distribution       recipient invitation/claim plus bounded durable bulk batches
  GiftCardPlatform.Modules.Sharing            protected links, reservations, claim/cancel/expiration
  GiftCardPlatform.Modules.Payments           QR/numeric credentials, POS identity, provisions, redemption, refunds
  GiftCardPlatform.Modules.Notifications      transactional delivery outbox, retry/backoff, dead-lettering
  GiftCardPlatform.Modules.Reporting          read-only financial, reconciliation, and owned-card queries
  *.Contracts                                 each module's public surface
tests/
  GiftCardPlatform.UnitTests
  GiftCardPlatform.IntegrationTests
  GiftCardPlatform.ArchitectureTests
infra/postgres/init/                          database role and privilege setup
```

A module may reference only another module's `.Contracts` project. Architecture
tests enforce this, along with domain purity (no EF Core, ASP.NET Core, Redis, or
Elasticsearch in `Domain` namespaces).
