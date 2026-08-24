# Backend Deployment Contract

This document describes the authoritative API member of `v0.5.0-rc.1`. It is a
source release-candidate contract. It does not create or claim ownership of a
hosting platform, DNS zone, certificate, secret manager, PostgreSQL service, or
notification provider.

## Topology

```text
portal BFF -----\
cardholder BFF --+-- HTTPS --> backend ingress --> GiftCardPlatform.Api
POS web --------/                              \--> giftcard PostgreSQL
```

Browsers never call the backend directly in production. The API remains a
bearer-only resource server with no broad CORS policy.

## Runtime configuration

```text
ASPNETCORE_ENVIRONMENT=Staging
ASPNETCORE_URLS=http://0.0.0.0:8080
AllowedHosts=api.<staging-domain>

ConnectionStrings__Default=Host=<backend-db-host>;Port=5432;Database=<backend-db>;Username=<app-role>;Password=<secret>;SSL Mode=Require
Authentication__Jwt__SigningKey=<independent-random-secret-at-least-32-bytes>
DataProtection__KeysPath=<absolute-shared-protected-key-directory>
Distribution__ClaimBaseUrl=https://card.<staging-domain>/activate
Sharing__ClaimBaseUrl=https://card.<staging-domain>/share/claim
Sharing__DirectClaimBaseUrl=https://card.<staging-domain>/activate/share
Partners__ClaimBaseUrl=https://card.<staging-domain>/epin
Partners__EpinDeliveryKey=<base64-encoded-random-32-byte-secret>
Partners__OrphanClaimLifetimeDays=365
Partners__MaximumEpinAmount=<commercially-approved-per-order-cap>
Partners__AuthRateLimit__PermitLimit=10
Partners__MintRateLimit__PermitLimit=<approved-per-client-window-count>
Partners__MintRateLimit__WindowSeconds=60

Networking__ForwardedHeaders__KnownProxies__0=<literal-portal-bff-ip>
Networking__ForwardedHeaders__KnownProxies__1=<literal-cardholder-bff-ip>
```

The application role must remain non-superuser and `NOBYPASSRLS`; the migration
owner is separate. `GIFTCARD_MIGRATIONS_CONNECTION` belongs only in the
deployment migration job, never the runtime process. Apply every committed
module migration before routing traffic, using the commands documented in
README.

`DataProtection:KeysPath` is required outside Development. Mount the same
durable protected directory into every API instance and restrict it to the API
runtime identity. The key ring protects credential-bearing notification outbox
payloads, so losing it makes queued messages unreadable. Back it up with the
database and test recovery as one operation. Do not share this directory with
the portal, cardholder, or POS applications; each uses a distinct application
name and key ring.

Audit checkpointing is disabled in this provider-neutral source candidate:

```text
Audit__Checkpoints__Enabled=false
Audit__Checkpoints__PollIntervalSeconds=300
Audit__Checkpoints__BatchSize=10000
```

Before enabling it outside Development, the deployment implementation must wire
`IAuditCheckpointSigner` to a non-exportable ECDSA P-256 key in an independently
administered KMS/HSM and `IAuditCheckpointWitness` to externally administered
WORM storage. This repository intentionally fails startup if a non-Development
environment enables checkpointing without that provider work; it never falls
back to a local key or mutable filesystem. Signer/witness IAM must be separate
from database administration. Alert on worker event 1901 (sealing delayed) and
event 1902 (verification failure). These failures do not make financial writes
unavailable, but the unsealed window is an operational incident.

Protected-share expiry is enabled by default. Keep
`Sharing__ExpirationEnabled=true`, choose bounded
`Sharing__ExpirationPollIntervalSeconds` and `Sharing__ExpirationBatchSize`,
and run at least one API worker instance. Multiple instances converge through
PostgreSQL locks and idempotent terminal state. The generic-link contract is
fixed at 24 hours and five failed PIN attempts; deployment configuration cannot
weaken those accepted security bounds.

`Bootstrap__PlatformAdministrator__Secret` is present only for the one-time
initial bootstrap and must be removed immediately after success. Use a secret
different from the JWT signing key and database passwords. Never commit any of
them or pass them in browser-visible configuration.

`Partners__EpinDeliveryKey` is credential-custody material, not an ordinary
application setting. Generate 32 random bytes, Base64-encode them, and store the
value in the deployment secret manager independently of JWT and Data Protection
keys. Keep it stable while an idempotent mint retry may need to reproduce the
same buyer link/PIN. Rotation therefore requires a versioned-key migration or
an agreed retry cutover; silently replacing it makes old retry responses
inconsistent with their persisted hashes.

The mint velocity limiter is a PostgreSQL fixed window partitioned by verified
partner client. Its atomic upsert uses database time, so adding API replicas does
not multiply the configured permit count. The counter table has forced RLS and
one bounded row per client. Keep a coarse source-IP flood limit at the public
ingress before authentication; never treat it as the authenticated quota. The
prepaid float and `MaximumEpinAmount` remain authoritative financial bounds.

For unclaimed clawback, first disable the affected client or reseller, then
call `POST /api/v1/platform/gift-cards/{giftCardId}/lifecycle/cancel` as an
operator with `platform.gift_cards.lifecycle.manage`, supplying a reason and
unique idempotency key. The normal lifecycle transaction refuses an already
claimed card, closes a pending invitation, returns remaining value once, and
records immutable lifecycle/audit evidence. Never delete invitation/card rows.

The backend accepts one `X-Forwarded-For` hop only from literal allowlisted
immediate BFF addresses. Each BFF must overwrite the header from its own trusted
ingress-derived connection address. Do not allow the public ingress or browser
to call this backend trust path directly unless that exact topology is reviewed.

## Backup and recovery drill

Use PostgreSQL native client tools; Docker is not required. The source database
is read only during the drill. Restore and cleanup are allowed only for an exact
database name beginning with `giftcard_recovery_test_`, and temporary artifacts
are confined to `.local/recovery-drill`.

```powershell
$env:POSTGRES_SUPERUSER_PASSWORD = '<administrator password>'
$keyRings = @(
    '<backend DataProtection key directory>',
    '<portal BFF DataProtection key directory>',
    '<cardholder BFF DataProtection key directory>',
    '<POS DataProtection key directory>'
)
./scripts/Test-PostgresRecovery.ps1 `
    -SourceDatabase '<source database>' `
    -KeyRingPath $keyRings
```

Run the database portion separately for the backend, portal session store, and
cardholder session store. POS has no PostgreSQL store, but its Data Protection
key ring must be included in one of the key-ring manifest runs.

The gate passes only when `pg_restore --exit-on-error` succeeds and the restored
catalog ownership, RLS flags, table row counts, sequence state, and SHA-256 key
manifests match the source. Record duration and output in the release evidence.
The existing Data Protection restart tests prove that preserved keys can
decrypt protected notification payloads. A staging drill must additionally
start each restored process, verify readiness, and exercise a real queued
notification and authenticated browser session before promotion.

Apply candidate migrations to the isolated restore, then probe both artifacts:

```powershell
./scripts/Test-BackendRollback.ps1 `
    -CurrentArtifact '<candidate publish directory>' `
    -RollbackArtifact '<previous publish directory>' `
    -ConnectionString '<runtime role connection to giftcard_recovery_test_*>' `
    -DataProtectionKeysPath '<restored backend key directory>'
```

Both artifacts must return `200` with `status: ready` against the same upgraded
database and key ring. This is a compatibility test, not permission to restore
removed safety controls. The accepted `f80bab8` baseline predates the shared
partner mint quota and pre-commit notification-channel guard. Before switching
traffic to it, the ingress must enforce an equivalent authenticated partner
quota and reject unsupported phone delivery routes, or those routes must be
disabled. Database migrations remain forward-only during an application
rollback; restore the pre-upgrade backup only for a separately approved disaster
recovery action.

## Coordinated release artifacts

Run the four-repository contract verifier and publish all application bundles
from the backend repository:

```powershell
.\scripts\Build-ReleaseArtifacts.ps1 `
  -SbomToolPath .\.local\tools\sbom-tool-v4.1.5.exe `
  -NoRestore
```

Use Microsoft SBOM Tool v4.1.5 from its official release. The builder accepts
only the executable with SHA-256
`625767B371B7FDD58F40F618B8A86DA0247A33C89E419039C86B4EDBA1DAD4B5`.
The release build refuses any dirty repository. `-AllowDirty` is reserved for
local rehearsal and records `rehearsal: true` plus each dirty state in the
artifact metadata. The output contains one versioned ZIP and one SPDX 2.2 SBOM
sidecar per application, plus `SHA256SUMS` and `ARTIFACTS.json`. Each ZIP has one
component root, the expected .NET entry point, an embedded SBOM matching its
sidecar, the identical compatibility contract, build commit metadata, and the
repository's operator documents. The builder rejects common secret,
private-key, Data Protection key-ring, and path-traversal names before reporting
success.

For promotion, publish only a clean run from the frozen commits. The coordinated
workflow creates GitHub provenance and SBOM attestations only for an intentional
`workflow_dispatch` run. After downloading, verify `SHA256SUMS`, then run
`gh attestation verify <archive> --repo open-giftcard/open-giftcard` for every
ZIP before deploying those exact archives to staging. A pull-request artifact,
source build, or `-AllowDirty` rehearsal is not substitutable release evidence.

## Native archive deployment

Docker is not required. Install the .NET 10 ASP.NET Core Runtime and PostgreSQL
client tools on the deployment and operator hosts. Node.js and pnpm are build
dependencies only; the portal archive already contains the compiled SPA under
the BFF's `wwwroot`.

Verify `SHA256SUMS` and the GitHub attestations before extraction. Expand each
ZIP into a versioned, read-only application directory. Keep configuration,
secrets, logs, databases, and Data Protection keys outside those directories.
Give backend, portal, cardholder, and POS separate protected key directories.
Portal and cardholder also require separate PostgreSQL databases with distinct
non-superuser migration-owner and runtime roles.

Apply migrations before starting or replacing runtime processes:

```powershell
$env:GIFTCARD_MIGRATIONS_CONNECTION = '<backend migration-owner connection>'
dotnet '<backend>\app\GiftCardPlatform.Api.dll' --migrate
Remove-Item Env:GIFTCARD_MIGRATIONS_CONNECTION

$env:ConnectionStrings__Portal = '<portal runtime connection>'
$env:ConnectionStrings__PortalMigrations = '<portal migration-owner connection>'
dotnet '<portal>\app\GiftCardPortal.Bff.dll' --migrate
Remove-Item Env:ConnectionStrings__PortalMigrations

$env:ConnectionStrings__Cardholder = '<cardholder runtime connection>'
$env:ConnectionStrings__CardholderMigrations = '<cardholder migration-owner connection>'
dotnet '<cardholder>\app\GiftCardCardholder.Web.dll' --migrate
Remove-Item Env:ConnectionStrings__CardholderMigrations
```

The long-running services receive runtime connections only. Run them under a
dedicated operating-system identity and supervisor, with an unprivileged local
HTTP listener behind an HTTPS reverse proxy:

```powershell
dotnet '<backend>\app\GiftCardPlatform.Api.dll'
dotnet '<portal>\app\GiftCardPortal.Bff.dll'
dotnet '<cardholder>\app\GiftCardCardholder.Web.dll'
dotnet '<pos>\app\GiftCardPos.Web.dll'
```

Set `ASPNETCORE_ENVIRONMENT=Staging` or `Production`, exact `AllowedHosts`, and
component-specific `ASPNETCORE_URLS`. Portal and cardholder use
`Backend__BaseUrl=https://api.<domain>`. POS uses
`Pos__BackendBaseUrl=https://api.<domain>` plus its one-time client secret,
client code, and terminal code. All four applications require a durable
`DataProtection__KeysPath` outside Development. Configure only the literal
immediate proxy addresses under `Networking__ForwardedHeaders__KnownProxies`.
The proxy must replace client forwarding headers, set `X-Forwarded-Proto`,
terminate TLS, bound request sizes and rates, and keep the backend off the
public browser path.

Deploy with a versioned-directory switch: start the candidate on unused local
ports, wait for `/health/ready`, switch the reverse-proxy upstream atomically,
and retain the previous application directory until the observation window
ends. Database migrations are forward-only. Application rollback is allowed
only after the documented compatibility probe and compensating ingress controls
for safety features absent from the old artifact.

## Health, logs, and promotion

Deployment is not required for local certification. On a developer machine
with bare PostgreSQL, run `scripts/Test-PartnerEpinLocal.ps1`; it creates a
name-guarded disposable test database, supplies the fixture's external
connection only for that process, and runs both backend and cardholder gates.
The administrator password is prompted and is not written to a file.

* `GET /health` — process liveness only.
* `GET /health/ready` verifies PostgreSQL connectivity and that all module
  migrations required by the running build are present. It returns 503 without
  connection detail when unavailable or behind.
* `/swagger` and `/demo` are not mapped outside Development.
* JSON request logs include the server-generated correlation ID, authenticated
  user, verified organization context, path, outcome, and duration. Audit rows
  remain the durable business/security record; logs are operational evidence.

Before promotion:

1. Verify the downloaded `v0.5.0-rc.1` artifact set and record all four embedded
   commit hashes, the shared release-contract hash, ZIP hashes, and SBOM hashes.
2. Apply migrations as the migrator, start the API as the app role, verify both
   health endpoints, and confirm Development-only endpoints return 404.
3. Bootstrap exactly one platform administrator if this is a fresh database,
   then remove the bootstrap secret.
4. Run portal and cardholder smoke journeys and correlate representative JSON
   request logs to append-only audit records.
5. Verify the runtime role is non-superuser/NOBYPASSRLS and cannot mutate audit
   history or bypass forced tenant policies.
6. When managed checkpoint adapters are supplied, verify one signed manifest in
   WORM storage independently with the recorded public key, then test the alert
   path for a missing or changed witness object.

Run the automated deployment gate from an operator host that can reach the five
public application endpoints and PostgreSQL using the backend runtime role. Put
smoke credentials in the documented `OPEN_GIFTCARD_SMOKE_*` environment
variables, not command history:

```powershell
.\scripts\Test-OpenGiftCardSmoke.ps1 `
  -EnvironmentName 'staging-rc1' `
  -BackendBaseUrl 'https://api.<staging-domain>' `
  -PortalUrl 'https://portal.<staging-domain>' `
  -PortalBffBaseUrl 'https://portal.<staging-domain>' `
  -CardholderBaseUrl 'https://card.<staging-domain>' `
  -PosBaseUrl 'https://pos.<staging-domain>' `
  -ArtifactManifestPath '<verified-download>\ARTIFACTS.json' `
  -EvidencePath '<new-evidence-directory>\automated-smoke.json'
```

The gate refuses HTTP, default local credentials, an auto-created POS client,
dirty or incomplete artifacts, checksum drift, mismatched embedded contracts,
and evidence overwrite. A passing record binds readiness, the non-superuser and
`NOBYPASSRLS` runtime role, forced RLS, payment confirmation, platform receipt,
full refund, and cardholder status to the exact four deployed commits. The JSON
and SHA-256 sidecar contain no password, token, payment credential, POS secret,
or database connection string. This record covers the automated staging smoke
row only; manual accessibility, infrastructure controls, SMTP, recovery, and
operator sign-off remain separate evidence.

Copy `STAGING_ACCEPTANCE.example.json` to the private evidence workspace after
the automated smoke completes. Record the named owner and non-secret evidence
reference for every product, accessibility, infrastructure, notification,
recovery, rollback, observability, and ingress check. Do not put screenshots,
tokens, connection strings, passwords, or secret-bearing URLs in the review
file. Bind the completed review to the automated result:

```powershell
.\scripts\New-StagingAcceptanceRecord.ps1 `
  -AutomatedSmokeEvidencePath '<new-evidence-directory>\automated-smoke.json' `
  -ReviewPath '<private-evidence-workspace>\staging-review.json' `
  -OutputPath '<new-evidence-directory>\staging-acceptance.json'
```

The command verifies the smoke SHA-256 sidecar and accepts only a passing named
HTTPS deployment record. It requires every fixed check exactly once, refuses a
future-dated or pre-smoke review, and writes a checksum-protected decision that
is bound to the exact artifact set and both input files. A rejected or
incomplete review is still recorded and then returns a failing exit code. The
script does not establish that an external reference is truthful; the named
reviewer and operator remain accountable for that evidence.

## Phase 3 operational checks

* Keep the bounded Sharing expiration worker enabled in every active API
  environment, or run an equivalent single-owner scheduled worker. Alert when
  it repeatedly fails; expired reservations otherwise remain unavailable until
  the next successful pass.
* Run each tenant's read-only reconciliation after migration, before promotion,
  and on an agreed operational schedule. A non-empty finding list blocks
  promotion and requires investigation; the endpoint deliberately performs no
  repair.
* Track `sharesChecked` and `activeReservationsChecked` with transaction/card
  counts. Sudden unexplained changes are operational signals, not values for a
  client to correct.
* A shared child is value transferred from a root issuance. Monitoring and
  exports must not count child `initial_value` as new corporate-funded issuance.

## Phase 4 operational checks

* Keep `Payments__Provisions__ExpirationEnabled=true`, a bounded
  `Payments__Provisions__ExpirationPollIntervalSeconds`, and a bounded
  `Payments__Provisions__ExpirationBatchSize`. The accepted two-minute
  provision window is startup-validated and must not be widened by deployment
  configuration.
* Provision POS clients and terminals through the permission-protected API.
  Capture each client secret once into the managed secret store; only its hash
  persists and there is no recovery endpoint.
* Treat POS access tokens and both 60-second payment credential forms as
  secrets. Do not log request bodies, QR values, or numeric codes at ingress,
  observability, analytics, or support boundaries.
* Before promotion, exercise one numeric or QR provision, confirmation, partial
  refund, and full reversal; reconcile the organization and verify platform POS
  totals and receipt lines against the immutable Ledger-backed records.
* Elasticsearch remains intentionally absent until measured volume or latency
  demonstrates a need. PostgreSQL reporting is the authoritative release path.

## Blocking external integration

The backend owns a transactional durable outbox and a bounded dispatcher. SMTP
is the reference email adapter; Development capture adapters support local
journeys and the delivery inspection endpoint remains Development-only. New
distribution, direct-share, and bulk work checks the configured sender before
commit. An unsupported channel returns `notification.channel.unconfigured`, and
an idempotent replay of work accepted earlier still returns its original result.
Add and certify an SMS adapter before enabling phone delivery. Neither frontend
may persist raw claim links or become a competing delivery authority.

Protected generic shares do not require backend notification delivery: the
sender receives the raw link and PIN once and sends them through separate
trusted channels. Direct email/phone shares reuse the same provider boundary.
Email can use the reference SMTP adapter; phone journeys remain blocked until an
external SMS integration exists.
