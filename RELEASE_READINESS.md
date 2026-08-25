# Deployment-Certified Release Candidate

This is the working gate for the first coordinated public Open Giftcard
candidate across the backend, portal, cardholder, and POS repositories. The
target name is `v0.5.0-rc.1`.

The target name is not a release claim. No canonical public repository had a
remote tag when this audit began on 2026-08-24. Local `v0.2` through `v0.4`
tags in older working copies point into retained legacy history and are not
public Open Giftcard releases.

## Certification boundary

The project can certify source, application behavior, published artifacts, and
a named staging deployment. It cannot certify an operator's TLS termination,
DNS, infrastructure access, backup retention, incident staffing, KMS, HSM, or
WORM provider without evidence from that deployment.

Every gate below therefore has one of four states:

- **Source verified:** enforced by committed tests or build automation.
- **Deployment verified:** exercised against the named staging environment and
  recorded in the release evidence.
- **Operator responsibility:** requires named ownership and external evidence.
- **Blocked:** required work or evidence is missing.

Nothing may be called production-ready while a required row remains blocked.
Passing CI alone is not deployment certification.

## Audit baseline

The milestone branches started from these public-history commits:

| Component | Canonical repository | Audit baseline | Backend contract pin |
| --- | --- | --- | --- |
| Backend | `open-giftcard/open-giftcard` | `f80bab86c63106c9ba4669bedc1850c0dec2a6cb` | Authoritative |
| Portal | `open-giftcard/open-giftcard-portal` | `71d69546a0e6af4b61ad5e3afddc9ddacbc470f3` | `e7bff3e0d39e1c24b89a6d39612ad5939d87f6e5` |
| Cardholder | `open-giftcard/open-giftcard-cardholder` | `4fac8a5203f2d5eb9577fd666339ffe02574ca1c` | `e7bff3e0d39e1c24b89a6d39612ad5939d87f6e5` |
| POS | `open-giftcard/open-giftcard-pos` | `ad902f01185dc5d7375637fa34598751a816c1b7` | `fbf3f7bd27479db66b7e3ae022576fc9db46278a` |

The implementation was frozen locally at these commits before updating the
non-self-referential release metadata:

| Component | Frozen implementation commit |
| --- | --- |
| Backend | `90c7dd2a17a6eb4ea686fc344de25f3a1a155d12` |
| Portal | `837dd91286951ef71ffe7a5992860272e1151e3c` |
| Cardholder | `6afd8b87ea261a1cf389845dc4488d4e10eaeac2` |
| POS | `4531ec0c602599cb6d4ff3643f9dbf01a2278360` |

The accepted backend commit serves OpenAPI SHA-256
`EC8051EBC7F65007DB8BBA6BDF1B84FAA3CDBA16456A96506E1BE562C3C3827C`.
All three client snapshots and all four compatibility manifests must carry that
same pin in their final metadata commits.

## Release gate

| Area | Required evidence | Current state | Owner |
| --- | --- | --- | --- |
| Four-repository compatibility | Exact backend, portal, cardholder, and POS commit manifest; identical accepted backend contract pin in every client | Source verified locally: all four manifests plus all three snapshots accept backend commit `90c7dd2` and OpenAPI SHA-256 `EC8051EB...`; hosted verification of the updated pin remains | Maintainer |
| Backend correctness | Release build; architecture and unit suites; complete real-PostgreSQL integration suite | Local totals are 243 unit, 15 architecture, and 398 integration tests; hosted verification of the updated candidate remains | Maintainer and CI |
| Client correctness | Release builds and full automated suites for portal, cardholder, and POS | Source verified at the current metadata commits: portal 104, cardholder 177, and POS 97 tests passed locally; hosted client CI evidence remains | Maintainer and CI |
| End-to-end transaction | Readiness for all five HTTP processes; runtime role check; forced RLS; recipient payment; POS confirmation; platform receipt; full refund | Source verified locally by `scripts/Test-OpenGiftCardSmoke.ps1`; the gate now accepts named HTTPS deployment endpoints, requires exact clean artifacts and pre-provisioned POS credentials, and emits checksum-protected redacted evidence; a real staging record remains | Maintainer |
| Database change control | Separate migration and runtime roles; forward migration; startup against upgraded schema; rollback policy | Local source verified: explicit migrators own DDL; populated upgrade exposed and fixed an RLS-hidden payment backfill plus Windows Event Log masking; current and f80bab8 artifacts both reached readiness on the upgraded isolated restore; staging evidence remains | Maintainer and operator |
| Multi-instance state | Shared sessions and Data Protection keys; restart and replica handoff tests | Data Protection implemented on milestone baselines; PostgreSQL partner mint quota proved across two API hosts locally; broader staging handoff evidence remains | Maintainer |
| Abuse controls | Quotas cannot be multiplied by adding API replicas; refusal behavior is tested | Source verified: partner mint quota is atomic, database-timed, RLS isolated, and returns 429 plus Retry-After across two API hosts; ingress flood-control evidence remains | Maintainer and ingress operator |
| Notification delivery | Accepted channels have a working provider and durable retry; unsupported channels fail before business acceptance | Source verified locally: unsupported phone distribution and async bulk acceptance return 400 before durable work; direct sharing and outbox use the same guard; SMTP staging evidence and an SMS adapter remain | Maintainer and notification operator |
| Audit custody | Checkpoint signing key outside database administration; immutable witness; restore and verification drill | `RemoteHttp` provider implemented against a custody gateway over mutual TLS, verified only against a stubbed transport; gateway, WORM retention, handshake, and verification drill have no staging evidence | Security operator |
| Secrets | No committed secrets; fail-closed configuration; rotation and revocation procedures | Source verified: POS client and terminal retirement are permanent, permission-protected, idempotently audited, and invalidate already-issued device tokens on their next request; replacement-client rotation is documented. Device-bound custody and installation evidence remain operator work | Maintainer and operator |
| Health and observability | Liveness, readiness, structured logs, metrics, alerts, correlation, runbooks | Source verified: stable OTLP exports bounded readiness, HTTP, worker, and audit metrics; six CI-checked Prometheus-compatible rules and an artifact-bound named-environment evidence gate ship in the backend archive. Collector deployment, live metric evidence, alert routing, and incident-path evidence remain operator work | Maintainer and operator |
| Backup and recovery | Database plus key-ring backup; timed restoration; restored credential/session verification | Local source verified: backend restore passed in 18.7s with all four key rings; separate portal and cardholder database/key restores passed in 3.0s and 3.1s; ownership, RLS flags, rows, sequences, and SHA-256 manifests matched; staging session evidence remains | Operator |
| Deployment artifacts | Reproducible, versioned, immutable artifacts for all four applications; provenance and SBOM | Local clean packaging and executable probes are verified; coordinated run `32726035561` produced the four clean hosted bundles with embedded and sidecar SPDX 2.2 SBOMs from the hash-pinned generator; an intentional dispatch with attestations remains | Maintainer and CI |
| Staging security | HTTPS, forwarded-header trust, secure cookies, CSP, no-store, least-privilege DB roles, dependency and code scanning | Source checks refuse insecure non-local smoke endpoints and verify a least-privilege runtime role plus forced RLS; no named staging environment has supplied TLS, proxy, cookie, header, or scanning evidence | Maintainer, security, and operator |
| Human acceptance | Portal, cardholder, and POS primary journeys; keyboard, mobile, zoom, reduced motion, screen reader, and visual review | The fixed 17-check acceptance schema and checksum-protected recorder are implemented; a named human and operator pass is not recorded | Maintainer and reviewer |
| Release publication | Clean public histories, signed or protected tags, release notes, compatibility manifest, upgrade and rollback notes | Blocked: canonical repositories have no public tags; rollback needs documented quota and phone-route compensating controls | Maintainer |

## Required structural slices

Complete these in order unless a later finding changes the dependency:

1. **Release contract and evidence format.** The machine-readable four-repo
   compatibility contract and local/CI verifiers are implemented. Freeze the
   final commits and generate the non-self-referential evidence manifest before
   tagging.
2. **Managed client schema migrations.** Implemented for portal and cardholder:
   normal startup performs no DDL, explicit checksum-protected migrators use
   separate owner connections, and the local stack runs them before startup.
   Record the staging upgrade and rollback evidence before tagging.
3. **Distributed abuse controls.** The authenticated partner mint quota is now
   shared in PostgreSQL and locally proved across two API hosts. Record the
   public-ingress flood-control configuration and staging evidence.
4. **Channel and custody safety.** Unsupported notification channels now fail
   before accepting delivery work. POS replacement-client rotation and
   immediate client/terminal retirement are now defined; certify SMTP and the
   device-store installation procedure in staging.
5. **Release artifacts.** The coordinated non-Docker builder now emits and
   validates four versioned artifacts, checksums, build metadata, and the shared
   manifest. Freeze clean commits, then add SBOMs and CI provenance to the final
   artifact run.
6. **Staging and recovery certification.** Deploy the exact candidate, run the
   artifact-bound automated smoke gate plus manual acceptance, restart replicas,
   restore the database and key rings, and exercise rollback. The redacted
   machine-readable smoke record and acceptance recorder are implemented; the
   named deployment and named review are still required.
7. **Public candidate.** Record final commits and evidence, update supported
   versions, create the four matching tags, and publish release notes.

## Deliberate non-goals for this candidate

- A full retail POS with catalogue, tax, stock, cash drawer, or offline mode.
- A native cardholder mobile application.
- A provider-specific cloud stack in the application repositories.
- Claiming that a source artifact proves an operator's organizational controls.

## Final evidence pack

The final candidate must publish or link:

- the exact four commit SHAs and one backend OpenAPI SHA-256;
- CI run links and test totals for every repository;
- artifact names, digests, SBOMs, and provenance;
- database migration, upgrade, rollback, backup, and restore results;
- staging URLs or private evidence identifiers without secrets;
- automated smoke output with credential values removed;
- manual acceptance sign-off and known limitations;
- operator ownership for every deployment-responsibility row;
- the four matching public tag references.
