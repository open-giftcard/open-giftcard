# Code Map

Lookup index for "where does X live / what does X do". **Check here before
searching the repository.** Keep it current: when you add, move, or rename a
public type, endpoint, permission, schema object, or module, update the relevant
row in the same change. Do not record implementation detail that changes often —
this file is a map, not documentation.

Status: reflects completed IMPL-001 through IMPL-034: Phase 3 sharing plus QR
and numeric payment tokens, POS authentication, provisions, redemption, and
partial refunds, followed by the organization card register and durable
asynchronous bulk batches.

---

## 1. Runtime shape

```text
HTTP → Api/Endpoints/*  → <Module>.Contracts interface
                        → <Module>/Application/*Service   (authorization enforced HERE)
                        → <Module>/Infrastructure/*DbContext
                        → PostgreSQL (RLS enforced HERE)
```

Authorization is enforced in the application service by named permission, never
in the endpoint, so it stays valid outside HTTP.

---

## 2. Where things live

### API host — `src/GiftCardPlatform.Api/`

| File | Contains |
| --- | --- |
| `docs/PHASE_3_PLAN.md` | Accepted Sharing reservation/protection contract, independently shippable slice order, cross-repository boundary, and phase exit gates |
| `docs/DEPLOYMENT.md` | Phase 2 staging topology, exact runtime configuration, probes, promotion gates, and notification blocker |
| `Program.cs`, `DataProtectionConfiguration.cs` | Host and DI wiring, fail-closed durable notification key-ring configuration, registered email/SMS channel capabilities, explicit one-hop known-proxy forwarded-address processing, correlation-id middleware, JWT/login/bootstrap/claim rate limiting, validated expiration and async-bulk worker options, Swagger, endpoint mapping |
| `Services/GiftCardExpirationWorker.cs` | Bounded trusted-system loop that routes due cards through the normal idempotent lifecycle service |
| `Services/BulkGiftCardBatchWorker.cs` | Bounded trusted-system loop that processes durable bulk rows in fresh scopes and leaves transient conflicts pending |
| `Services/ShareExpirationWorker.cs` | Bounded trusted-system loop that closes due pending shares without posting Ledger value |
| `scripts/Test-PostgresRecovery.ps1` | Native PostgreSQL guarded backup/restore drill with catalog, RLS, row, sequence, and multi-key-ring manifest comparison |
| `scripts/Test-BackendRollback.ps1` | Production-mode readiness probe for candidate and rollback artifacts against one guarded upgraded database and restored key ring |
| `Authentication/BearerAuthentication.cs` | JWT validation with persisted platform-permission or verified organization-context resolution in every environment |
| `Endpoints/BootstrapEndpoints.cs` | One-time platform-administrator bootstrap and initial root-organization administrator assignment |
| `Endpoints/IdentityEndpoints.cs` | User create/disable plus login, refresh, and revoke endpoints and explicit API contracts |
| `Endpoints/CorporateCreditEndpoints.cs` | Corporate-credit allocation, reversal, balance, and cursor-history endpoints with explicit API contracts |
| `Endpoints/GiftCardEndpoints.cs` | Issuance/inventory plus organization, platform, and cardholder lifecycle command/history endpoints with explicit API contracts |
| `Endpoints/DistributionEndpoints.cs` | Organization single/synchronous bulk distribution, async accept/paged-status/retry, no-store anonymous claim with optional new-identity session, and Development-only delivery lookup endpoints |
| `Endpoints/SharingEndpoints.cs` | Exact-owner protected/direct create, filter-bound sender/recipient history, cancel, and recipient claim endpoints; one-time credentials use no-store responses |
| `Endpoints/ReportingEndpoints.cs` | Organization financial summary/Sharing-aware history/read-only reconciliation, exact-owner My Cards/detail/history, tenant audit investigation, and platform POS payment/receipt reporting endpoints |
| `Endpoints/CurrentUserEndpoints.cs` | Authenticated identity/context composition and exact-user organization-picker endpoints |
| `Endpoints/OrganizationEndpoints.cs` | Root-organization create/read plus permission-gated, searchable platform customer listing |
| `Endpoints/MembershipEndpoints.cs` | `MapMembershipEndpoints`, `CreateMembershipApiRequest`, `MembershipApiResponse` |
| `Endpoints/SubsidiaryEndpoints.cs` | `MapSubsidiaryEndpoints`, `CreateSubsidiaryApiRequest`, `SubsidiaryApiResponse` |
| `Endpoints/PaymentEndpoints.cs`, `PosEndpoints.cs` | Exact-owner QR credential issuance and credential-free checkout-status read, plus platform-gated POS client/terminal registration and anonymous POS credential exchange; secret/status responses use no-store |
| `Endpoints/DemoEndpoints.cs`, `Demo/index.html` | Embedded development-only Phase 3 console at `/demo`; adds protected-share creation/claim/history/cancellation and posted/reserved/available value to the existing public JWT/API journeys |
| `Errors/AppExceptionHandler.cs` | Maps `AppException` subtypes and safe structured extensions to ProblemDetails; **records `authorization.denied` audit for authenticated denials** (ADR-025) |
| `ApiRoutes.cs` | `ApiRoutes.V1` — the single source of the `/api/v1` route prefix (ADR-027) |
| `RequestLog.cs` | Source-generated request-completion logging, scoped by correlation id |

### Shared primitives — `src/GiftCardPlatform.BuildingBlocks/`

| File | Contains |
| --- | --- |
| `Execution/IExecutionContext.cs` | Trusted caller context: user/platform/system/POS-device authority, verified tenant scope, exact distribution/share/payment-token/numeric-code candidates, correlation id |
| `Execution/MutableExecutionContext.cs` | Scoped settable impl: organization, distribution claim, protected-share claim, verified member, identity, platform, system, POS device, and anonymous transitions; every non-POS setter clears the POS principal |
| `Execution/SystemActorIds.cs` | Stable non-human identifiers for internally attributed work such as gift-card expiration, protected-share expiration, and async bulk processing |
| `Persistence/TransactionCoordinator.cs` | `ITransactionCoordinator` / `IModuleTransaction` — one Npgsql transaction shared across module DbContexts; **joins if one is in progress** (only the outermost commits); optional `IsolationLevel`; writes session context on begin; rolls back on dispose without commit |
| `Persistence/SessionContextWriter.cs` | Writes user/organization/platform plus exact claim/share/payment-token/numeric-code candidates via transaction-local `set_config` |
| `Persistence/ScopedDatabaseConnection.cs` | One `NpgsqlConnection` per scope, shared by all module DbContexts |
| `Persistence/DatabaseConnectionFactory.cs` | `IDatabaseConnectionFactory` — fresh connections for work that must survive a rollback (denial audit) |
| `Persistence/TenantDbFunctions.cs` | Shared EF mapping for `organizations.organization_belongs_to_caller_tenant(uuid)` so module filters mirror hierarchy-aware RLS |
| `Errors/ApplicationErrors.cs` | `AppException` base + validation 400, unauthorized 401, forbidden 403, not-found 404, conflict 409 |
| `BuildingBlocksServiceCollectionExtensions.cs` | `AddBuildingBlocks(connectionString)` |

### Organizations module — `src/GiftCardPlatform.Modules.Organizations/`

| File | Contains |
| --- | --- |
| `Domain/Organization.cs` | `Organization` aggregate (`CreateRoot`, `CreateSubsidiary`), `OrganizationStatus`, `RootOrganizationId` (tenant code namespace, ADR-024) |
| `Domain/OrganizationCode.cs` | Code normalization + validation (`organization.code.{required,invalid_length,invalid_format}`) |
| `Domain/OrganizationHierarchy.cs` | ltree label/path helpers, `RootDepth`, `DefaultMaxDepth`, `CreateChildPath` |
| `Domain/OrganizationMembership.cs` | `OrganizationMembership` (`Create`, `Disable`), `OrganizationMembershipStatus` — **tenant-owned** |
| `Application/OrganizationService.cs` | Create root org, get org; platform-permission gated |
| `Application/OrganizationDiscoveryQuery.cs` | Current-user membership discovery, selected-context permission composition, and platform root-customer search |
| `Application/MembershipService.cs` | Create / list / disable membership; org-permission gated + platform read path |
| `Application/SubsidiaryService.cs` | Create / list subsidiaries; org-permission gated, parent from execution context |
| `Application/ActiveMembershipResolver.cs` | Authentication lookup: resolves the unique active membership and verified tenant root for `(user, organization)` through an independent RLS-scoped connection |
| `Application/InitialAdministratorMembershipProvisioner.cs` | Restricted cross-tenant write path that validates an active root and creates/reuses its initial administrator membership |
| `Application/OrganizationFinancialEligibilityQuery.cs` | Narrow cross-module checks for an active root financial recipient and an active operational issuer within that funding hierarchy |
| `OrganizationHierarchyOptions.cs` | Configurable `MaxDepth` (section `Organizations:Hierarchy`), validated at registration |
| `Infrastructure/OrganizationsDbContext.cs` | `organizations` schema, DbSets, hierarchy-aware membership tenant filter plus exact-user pre-selection filter |
| `Infrastructure/OrganizationConfiguration.cs` | Org table, check constraints, unique code index |
| `Infrastructure/OrganizationMembershipConfiguration.cs` | Membership table, unique `(organization_id, user_id)`, and active exact-user discovery index |
| `Infrastructure/Migrations/*AddFrontendOrganizationDiscovery*` | SELECT-only exact-user active-membership/organization RLS for pre-selection discovery |
| `OrganizationsModuleExtensions.cs` | `AddOrganizationsModule`, `MigrateOrganizationsModuleAsync` |

Public surface: `src/GiftCardPlatform.Modules.Organizations.Contracts/`
— `OrganizationContracts.cs` (`IOrganizationService`,
`IOrganizationDiscoveryQuery`, and root/current-user discovery records),
`MembershipContracts.cs` (`IMembershipService`, `CreateMembershipRequest`, `MembershipResult`),
`SubsidiaryContracts.cs` (`ISubsidiaryService`, `CreateSubsidiaryRequest`, `SubsidiaryResult`), and
`ActiveMembershipContracts.cs` (`IActiveMembershipResolver`) and the
`IInitialAdministratorMembershipProvisioner` boundary in `MembershipContracts.cs`.

### Audit module — `src/GiftCardPlatform.Modules.Audit/`

| File | Contains |
| --- | --- |
| `Domain/AuditRecord.cs` | Append-only audit row, including active-membership attribution for organization actors |
| `Domain/AuditCheckpoint.cs`, `Domain/AuditCheckpointCryptography.cs` | Versioned checkpoint/seal/receipt entities, canonical leaf and manifest encoding, SHA-256 Merkle roots, signed-manifest serialization, and ECDSA verification |
| `Application/AuditRecorder.cs` | `IAuditRecorder` impl — `RecordAsync` **requires an in-progress module transaction**; `RecordIndependentlyAsync` uses its own connection so denial records survive a rollback; both take the shared checkpoint-boundary lock |
| `Application/AuditCheckpointProcessor.cs`, `AuditCheckpointLock.cs` | Three-stage manifest/sign/witness pipeline, offline verification, and shared-writer/exclusive-sealer PostgreSQL advisory locking |
| `Application/AuditInvestigationQuery.cs` | Dedicated-permission, stable-cursor tenant/platform investigation with exact operation/outcome/correlation filters |
| `Infrastructure/AuditDbContext.cs`, `AuditCheckpointConfigurations.cs` | `audit` schema, tenant/identity record filter, database sequence, and append-only checkpoint evidence mappings |
| `Infrastructure/Migrations/*AddAuditInvestigationIndex*` | Stable `(organization_scope_id, occurred_at_utc, id)` investigation index |
| `Infrastructure/Migrations/*AddAuditTamperEvidence*` | Sequence/backfill, checkpoint evidence tables, constraints, indexes, and upgrade-safe runtime sequence grant |
| `AuditModuleExtensions.cs` | `AddAuditModule`, `MigrateAuditModuleAsync` |

Host-side `Api/Services/AuditCheckpointWorker.cs` runs one complete bounded
checkpoint per interval and verifies all sealed evidence. The adjacent
`DevelopmentAuditCheckpointAdapters.cs` supplies explicit local PEM/file
adapters only when Development configuration opts in.

Public surface: `Modules.Audit.Contracts/AuditContracts.cs` — `IAuditRecorder`,
`IAuditInvestigationQuery`, `AuditEntry`, investigation request/page records,
`AuditActorType`, `AuditOutcome`, `AuditOperations`, checkpoint options,
provider-neutral signer/witness contracts, processor results, and verifier.

### Authorization module — `src/GiftCardPlatform.Modules.Authorization/`

| File | Contains |
| --- | --- |
| `Domain/Role.cs` | `Role` aggregate (`Create`, `CreateSystem`, `Grant`) — owned by one organization, carries no scope |
| `Domain/RolePermission.cs` | A permission granted to a role; denormalized `OrganizationId` as the RLS key |
| `Domain/MembershipRoleAssignment.cs` | Assignment + `RoleScopeType` (Organization / Subtree / SelectedOrganizations) |
| `Domain/MembershipRoleAssignmentScope.cs` | One explicitly granted organization (the `SelectedOrganizations` relation) |
| `Domain/PermissionDefinition.cs` | **Global** permission catalogue row; name is the key |
| `Domain/PlatformRole.cs`, `PlatformRolePermission.cs`, `PlatformRoleAssignment.cs` | Global platform-role authorization model |
| `Domain/PlatformBootstrapState.cs` | Singleton durable one-time bootstrap lock/completion state |
| `Domain/OrganizationAdministratorBootstrap.cs` | Durable idempotency record for a root organization's first Company Administrator |
| `Application/RoleService.cs` | Create / list / grant / assign plus exact-organization assignment listing, permission-gated + audited |
| `Application/PermissionEvaluator.cs` | Effective permissions for a membership against a target organization |
| `Application/OrganizationPermissionAuthorizer.cs` | Application-service guard requiring a database-backed effective permission for the verified active membership and target organization |
| `Application/PlatformPermissionResolver.cs` | Effective persisted platform permissions for a JWT subject |
| `Application/PlatformBootstrapService.cs` | Secret-protected, row-locked one-time Platform Administrator creation |
| `Application/InitialOrganizationAdministratorService.cs` | Atomic initial root Company Administrator role, grant, scope, and audit workflow |
| `Application/PermissionCatalogueSynchronizer.cs` | Synchronizes known platform and organization permission names |
| `Infrastructure/AuthorizationDbContext.cs` | `authorization` schema and hierarchy-aware tenant query filters |
| `Infrastructure/AuthorizationConfigurations.cs` | Tenant and platform authorization entity configurations |
| `Infrastructure/Migrations/*AddAuditInvestigationPermissions*` | Adds dedicated audit-view definitions and grants them to existing system Company/Platform Administrator roles |
| `AuthorizationModuleExtensions.cs` | `AddAuthorizationModule`, `MigrateAuthorizationModuleAsync` (also seeds the catalogue) |

Public surface: `Modules.Authorization.Contracts/` — `PlatformPermissions.cs`,
`OrganizationPermissions.cs`, and `AuthorizationContracts.cs` (`IRoleService`,
`IPermissionEvaluator`, `IOrganizationPermissionAuthorizer`, `RoleScope`,
request/result records), plus `PlatformAuthorizationContracts.cs`
(`IPlatformPermissionResolver`, `IPlatformBootstrapService`,
`IInitialOrganizationAdministratorService`, options and request/result records).

### Identity module — `src/GiftCardPlatform.Modules.Identity/`

| File | Contains |
| --- | --- |
| `Domain/User.cs` | Global user account, exactly-one email/phone contact, and active/disabled lifecycle |
| `Domain/UserSession.cs` | Fixed 30-day refresh session and token-family identity |
| `Domain/RefreshToken.cs` | Hashed one-time refresh credential, consumption, replacement, and revocation state |
| `Application/CredentialPolicy.cs` | Normalized email/E.164 phone plus 12–128 Unicode password and common-password policy |
| `Application/TokenGenerator.cs` | Signed 15-minute JWTs, opaque refresh tokens, and SHA-256 refresh hashes |
| `Application/AuthenticationService.cs` | Nondisclosing login and narrow password-verified claim-session boundary, locked refresh rotation, reuse detection, session revocation |
| `Application/UserSessionTokenIssuer.cs` | Shared transaction-joining access/refresh session and credential issuance used by login and new-recipient claim |
| `Application/UserService.cs` | Platform-authorized user create/disable with atomic audit |
| `Application/IdentityBootstrapService.cs` | Internal first-platform-user creation boundary used only by the one-time bootstrap |
| `Application/IdentityUserQuery.cs` | Cross-module user lookup boundary for initial administrator assignment |
| `Application/OrganizationStaffDirectory.cs` | Permission-protected active staff-email resolution and authorized membership-email composition |
| `Application/RecipientIdentityService.cs` | Narrow claim boundary: reuse an exact active contact without password reset or create the minimum recipient identity |
| `Infrastructure/IdentityDbContext.cs` | Global `identity` schema: users, sessions, refresh tokens |
| `Infrastructure/IdentityConfigurations.cs` | Keys, partial email/phone uniqueness, exactly-one-contact check, state/expiry checks, session-family FK |
| `Infrastructure/Migrations/*InitialIdentity*` | Independent initial Identity migration |
| `IdentityModuleExtensions.cs` | `AddIdentityModule`, `MigrateIdentityModuleAsync` |

Public surface: `Modules.Identity.Contracts/IdentityContracts.cs` —
`IUserService`, `IAuthenticationService`, `IRecipientClaimSessionIssuer`,
`IIdentityBootstrapService`,
`IIdentityUserQuery`, `IOrganizationStaffDirectory`,
`IRecipientIdentityService`, request/result records, and `IdentityTokenOptions`.

### Ledger module — `src/GiftCardPlatform.Modules.Ledger/`

| File | Contains |
| --- | --- |
| `Domain/Money.cs` | Positive `decimal(20,4)` amount and normalized three-letter currency invariant |
| `Domain/LedgerAccount.cs` | Platform-funding, per-currency redemption-settlement, organization corporate-credit, and one-account-per-card value accounts |
| `Domain/LedgerTransaction.cs` | Immutable allocation/reversal/issuance/lifecycle/share/redemption/refund postings, positive debit/credit entries, per-currency balancing, intent hashes |
| `Application/LedgerWriter.cs` | Server-selected accounts, financial idempotency, account-scoped advisory locking, exact entry-derived gift-card value return, serializable posting boundary, and dedicated accepted-batch system attribution |
| `Application/GiftCardPaymentLedger.cs` | Credential-scoped locked balance, `gift_card.redemption`, and inverse `gift_card.refund`; exact server-derived candidates and common lock order |
| `Application/LedgerBalanceQuery.cs` | RLS-scoped organization corporate-credit balances derived from immutable entries |
| `Infrastructure/LedgerDbContext.cs`, `LedgerConfigurations.cs` | `ledger` schema, query filters, constraints, indexes |
| `Infrastructure/Migrations/*InitialLedger*`, `*AddGiftCardOwnerReadPolicies*`, `*AddRedemptionSettlement*`, `*AddPaymentRefundLedgerPolicy*` | Accounts/transactions/entries, forced RLS, settlement type, and exact POS redemption/refund candidates |
| `LedgerModuleExtensions.cs` | `AddLedgerModule`, `MigrateLedgerModuleAsync` |

Public surface: `Modules.Ledger.Contracts/LedgerContracts.cs` —
`ILedgerWriter`, `ILedgerBalanceQuery`, `IGiftCardPaymentLedger`, financial
requests/results, redemption posting records, and ledger-derived balances.

### Corporate Credits module — `src/GiftCardPlatform.Modules.CorporateCredits/`

| File | Contains |
| --- | --- |
| `Domain/CorporateCreditIntent.cs` | Allocation input normalization and financial validation |
| `Domain/CorporateCreditAllocation.cs` | Immutable allocation record linked to its ledger transaction |
| `Domain/CorporateCreditReversal.cs`, `CorporateCreditReversalIntent.cs` | Immutable correction record, input normalization, original-allocation link |
| `Application/CorporateCreditAllocationService.cs` | Named platform permission, active-root check, idempotent serializable ledger/audit workflow |
| `Application/CorporateCreditReversalService.cs` | Sufficient-balance check, compensating posting, idempotency, atomic audit |
| `Application/CorporateCreditQueryService.cs` | Platform/customer authorization, balance projection, stable cursor allocation history |
| `Infrastructure/CorporateCreditsDbContext.cs` | `corporate_credits` schema and tenant query filter |
| `Infrastructure/Migrations/*InitialCorporateCredits*` | Allocation table, forced RLS, uniqueness and money constraints |
| `CorporateCreditsModuleExtensions.cs` | `AddCorporateCreditsModule`, `MigrateCorporateCreditsModuleAsync` |

Public surface:
`Modules.CorporateCredits.Contracts/CorporateCreditContracts.cs` —
`ICorporateCreditAllocationService`, `ICorporateCreditQueryService`, allocation
records, balance results, and cursor history contracts.

### Gift Cards module — `src/GiftCardPlatform.Modules.GiftCards/`

| File | Contains |
| --- | --- |
| `Domain/GiftCardIssuanceIntent.cs` | Amount/currency/validity/policy/reference normalization plus tenant-namespaced Ledger idempotency |
| `Domain/GiftCard.cs` | Funded card identity, ownership transitions, server-time validity, suspend/reactivate/cancel/expire lifecycle, ledger links, policy, actor, and lineage |
| `Domain/GiftCardLifecycleIntent.cs`, `GiftCardLifecycleEvent.cs` | Normalized administrative/owner/system command intent and immutable actor/state/value-return history |
| `Application/GiftCardIssuanceService.cs` | Exact-target permission, active issuer, serializable ledger/card/audit workflow, idempotent retry, and a system-only accepted-batch path retaining the accepting member's attribution |
| `Application/GiftCardIssuanceRequestValidator.cs` | Side-effect-free normalization/preflight reused by single and bulk issuance |
| `Application/GiftCardInventoryQuery.cs` | Permission-checked owner inventory with stable opaque cursor |
| `Application/GiftCardOwnershipWriter.cs` | Narrow Distribution boundary for serialized begin-distribution and complete-claim ownership transitions, including the dedicated accepted-batch system actor |
| `Application/GiftCardLifecycleService.cs` | Organization/platform/owner/system authority, serialized transitions, invitation closure, exact Ledger return, lifecycle event and audit atomicity |
| `Application/GiftCardLifecycleHistoryQuery.cs` | Organization/platform/exact-owner lifecycle history and current-card read |
| `Application/GiftCardExpirationProcessor.cs` | Bounded due-card selection and idempotent trusted-system finalization |
| `Application/GiftCardPublicReferenceGenerator.cs` | `GC-` display/support reference with 80 random bits; never a payment credential |
| `Infrastructure/GiftCardsDbContext.cs` | `gift_cards` schema and tenant-or-identity-owner query filter |
| `Infrastructure/GiftCardConfiguration.cs` | Ownership/lifecycle coherence, immutable event financial/actor transitions, money, validity, provenance, idempotency, and `xmin` constraints/indexes |
| `Infrastructure/Migrations/*GiftCards*` | Gift-card/event tables, distribution ownership and lifecycle states, forced tenant/owner/claim RLS, append-only and terminal-state triggers |
| `GiftCardsModuleExtensions.cs` | `AddGiftCardsModule`, `MigrateGiftCardsModuleAsync` |

Public surface:
`Modules.GiftCards.Contracts/GiftCardContracts.cs` —
`IGiftCardIssuanceService`, `IAcceptedBulkGiftCardIssuanceService`,
`IGiftCardIssuanceRequestValidator`,
`IGiftCardInventoryQuery`,
`IGiftCardLifecycleService`, `IGiftCardLifecycleHistoryQuery`,
`IGiftCardExpirationProcessor`, request/result/options records, cursor
inventory contracts, and the restricted `IGiftCardOwnershipWriter`
cross-module boundary.

### Distribution module — `src/GiftCardPlatform.Modules.Distribution/`

| File | Contains |
| --- | --- |
| `Domain/DistributionIntent.cs` | Email/E.164 phone normalization, masked contacts, references, idempotent intent |
| `Domain/ClaimTokenCodec.cs` | 256-bit invitation tokens, token parsing, SHA-256 hashing, fixed-time verification |
| `Domain/DistributionInvitation.cs` | Pending/Claimed/Locked/Expired/Cancelled state, failed-attempt bound, expiry, distribution/claim idempotency, terminal card closure |
| `Domain/DistributionEvent.cs` | Append-only distribution, claim, and card-cancelled/expired closure history |
| `Domain/BulkGiftCardBatchIntent.cs`, `BulkGiftCardBatch.cs` | Max-100 synchronous and max-2,000 asynchronous normalized intent, deterministic child keys, Pending/Processing/Completed counts, one-way row outcomes, and immutable child retries |
| `Application/GiftCardDistributionService.cs` | Exact permission, serialized invitation/card/event/audit transaction, pre-write notification-channel guard, and a system-only accepted-batch path retaining member attribution |
| `Application/BulkGiftCardBatchService.cs`, `BulkGiftCardBatchMapping.cs` | Dual-permission synchronous/async acceptance, pre-acceptance channel guard, idempotent child retry, masked cursor-paged status, and stable result mapping |
| `Application/BulkGiftCardBatchProcessor.cs` | Per-row serializable processing, `FOR UPDATE SKIP LOCKED` claims, durable business failures, and transient-conflict retry in fresh scopes |
| `Application/GiftCardClaimService.cs` | Claim-candidate RLS, secret verification, atomic identity/card/invitation/event/audit transition, and new-identity-only session composition |
| `Application/DistributionLifecycleWriter.cs` | Narrow serialized lifecycle closure boundary sharing claim's invitation lock order |
| `Application/OutboxGiftCardClaimNotifier.cs` | Transactional notification enqueue with protected recipient/link payload and sender-capability enforcement |
| `Application/DevelopmentClaimDeliveryQuery.cs` | Permission-checked Development-only delivery lookup |
| `Infrastructure/DistributionDbContext.cs`, `DistributionConfigurations.cs` | `distribution` schema, tenant/claimant filters, immutable invitation/event history, durable async intent/outcomes, counts, and retry lineage |
| `Infrastructure/Migrations/*InitialDistribution*`, `*AddBulkGiftCardBatches*`, `*AddAsyncBulkGiftCardBatches*`, `*AddCardholderDistributionHistoryRead*` | Invitation/event and bulk batch/item tables, forced RLS, durable intent backfill, source/coherence checks, one-way settlement triggers, and exact-claimant full event-history read |
| `DistributionModuleExtensions.cs` | Options, services, DbContext, `AddDistributionModule`, migration |

Public surface:
`Modules.Distribution.Contracts/DistributionContracts.cs` —
`IGiftCardDistributionService`, `IBulkGiftCardBatchService`,
`IBulkGiftCardBatchProcessor`, async page/summary/processing contracts and
`BulkGiftCardBatchOptions`,
`IGiftCardClaimService`,
`IGiftCardClaimNotifier`, Development delivery query, request/result records,
`RecipientContactType`, and `DistributionOptions`.

### Reporting module — `src/GiftCardPlatform.Modules.Reporting/`

| File | Contains |
| --- | --- |
| `Application/FinancialReportingQuery.cs` | Parameterized organization summary/history/reconciliation and exact-owner card views; gross spent, refunded, and net spent remain explicit and rebuildable |
| `Application/FinancialHistorySearchFilters.cs` | Bounded organization-history filter normalization, literal PostgreSQL pattern escaping, UTC range validation, and deterministic filter fingerprinting |
| `Application/PaymentReportingQuery.cs` | Permission-gated receipt/payment/refund rows, currency-safe page/all-match totals, and ordered refund detail over authoritative Payments tables |
| `Application/PaymentReportingSearchFilters.cs` | Exact POS/store/tenant/state/currency normalization, literal reference escaping, UTC validation, and deterministic filter fingerprints |
| `Application/OrganizationCardRegisterQuery.cs` | Organization register of every funded card across all ownership states; suppresses remaining balance in SQL for identity-owned cards (ADR-052) |
| `Application/OrganizationCardRegisterFilters.cs` | Closed-set lifecycle/ownership/currency matching, literal reference escaping, and filter fingerprinting |
| `Application/ReportingCursorCodec.cs` | Strict base64url v1 unfiltered and filter-bound v2 `(occurred_at_utc, stable_key)` cursor encoding/validation |
| `ReportingModuleExtensions.cs` | Registers financial and POS payment reporting queries; Reporting owns no DbContext, schema, migrations, or writes |

Public surface:
`Modules.Reporting.Contracts/ReportingContracts.cs` —
`IFinancialReportingQuery`, `IPaymentReportingQuery`, `IOrganizationCardRegisterQuery`,
dedicated organization and
payment search requests,
cardholder page requests/results, per-currency summaries, cross-operation history
items, owned-card records, receipt/refund detail, currency-safe payment totals,
and share/reservation-aware reconciliation findings.

### Sharing module (`src/GiftCardPlatform.Modules.Sharing/`)

| File | Contains |
| --- | --- |
| `Domain/GiftCardShare.cs` | Protected-link/direct-invitation kind, Pending/Claiming/Claimed/Cancelled/Expired/Locked lifecycle, reservation intent, bounded PIN attempts, contact binding, idempotent claim/cancel, and terminal source closure |
| `Domain/ShareTokenCodec.cs`, `SharePinCodec.cs` | 256-bit opaque token parsing/SHA-256 verification and PBKDF2-SHA256 six-digit PIN creation/verification |
| `Domain/GiftCardShareEvent.cs` | Append-only create, failed-PIN, lock, claim, cancel, and expiry history |
| `Application/GiftCardShareService.cs` | Exact-owner generic/direct reservation/create/filter-bound list/cancel, pre-write notification-channel guard, authenticated PIN claim, verified-contact Identity activation, RLS-visible card references, atomic transfer, expiry/source closure, audit, lock order, and concurrency-to-409 translation |
| `Application/OutboxDirectGiftCardShareNotifier.cs`, `DevelopmentDirectGiftCardShareDelivery.cs` | Transactional protected enqueue plus Development-only sender-authorized masked delivery lookup |
| `Infrastructure/SharingDbContext.cs`, `SharingConfigurations.cs` | `sharing` schema, exact sender/recipient/candidate filters, checks, indexes, and optimistic concurrency |
| `Infrastructure/Migrations/*InitialSharing*`, `*AddDirectRecipientSharing*` | Shares/events, protected/direct constraints and upgrade backfill, forced RLS, immutable share/contact identity, append-only events, grants-compatible schema |
| `SharingModuleExtensions.cs` | Exact 24-hour/five-attempt option validation, services, DbContext, and migration |

Public surface: `Modules.Sharing.Contracts/SharingContracts.cs` provides
protected/direct requests/results/page, notification/development-delivery
boundaries, reservation reads, lifecycle closure, expiration, and validated
options. Identity owns recipient contact normalization and resolve/create/session
policy. Gift Cards implements eligibility/child
lineage; Ledger implements locked balance and the balanced
`gift_card.share_transfer`. Their policy migrations narrow RLS to `app.share_id`.

### Partners module (`src/GiftCardPlatform.Modules.Partners/`)

E-pin resellers (BynoGame, Eneba, Kabasakal) that mint gift cards from their own
checkout and never use the portal. Registry, credential exchange, atomic
partner-funded mint plus orphan link/PIN delivery, buyer claim, caps, rate
policy, kill switches, and the existing unclaimed lifecycle clawback are built.

| File | Contains |
| --- | --- |
| `Domain/Partner.cs` | Reseller identity anchored to the funding root organization, normalized code, active/disabled kill switch |
| `Domain/PartnerApiClient.cs` | One machine credential per row, globally unique code, hashed secret, per-key disable for rotation and leak response |
| `Application/PartnerMintQuota.cs` | PostgreSQL fixed-window mint budget using database time and an atomic upsert, shared across API replicas |
| `Infrastructure/PartnerMintRateWindow.cs`, `Migrations/*MintRateWindows*` | One bounded counter row per partner client with forced RLS and runtime insert/update privileges |
| `Domain/PartnerCredentialCodec.cs` | 256-bit CSPRNG secret, SHA-256 hex store, constant-time compare that refuses a malformed hash rather than throwing |
| `Application/PartnerRegistrationService.cs` | Platform-gated partner and API-client registration, one-time secret disclosure, per-client and per-partner kill switch, atomic audit |
| `Application/PartnerAuthenticationService.cs` | Constant-time credential exchange with uniform refusals, short-lived identity-only JWT, and the per-request principal resolver that makes disabling immediate |
| `Application/PartnerCredentialThrottle.cs` | Per-client failure window keyed on the resolved client id, so guessing one credential costs only that credential its budget; the endpoint IP limiter cannot isolate resellers and a caller-supplied key would be spoofable |
| `Infrastructure/PartnersDbContext.cs`, `PartnersConfigurations.cs` | `partners` schema, tenant query filters mirroring RLS, code/status/hash-shape checks, unique code and one-partner-per-tenant indexes |
| `Infrastructure/Migrations/*InitialPartners*` | Partners/api_clients, forced RLS with a read-only credential-lookup escape, immutable identity triggers, no runtime DELETE |
| `PartnersModuleExtensions.cs` | Validated options, DbContext, migration |

`Distribution/Application/PartnerEpinService.cs` composes issuance and orphan
invitation creation in one serializable transaction. `Domain/EpinCredentialCodec.cs`
derives stable retry-safe delivery credentials from a managed key while the
database retains only hashes. `DistributionInvitation` distinguishes directed
contact delivery from `OrphanPin`, and buyer claim either binds to the already
authenticated identity or creates a new contact/password identity.

Public surface: `Modules.Partners.Contracts/PartnerContracts.cs` provides
partner/client statuses, results, the one-time secret disclosure record,
registration and credential-exchange boundaries, `IPartnerPrincipalResolver`,
the partner token claim names, and validated options. Unlike POS clients, a partner is tenant-owned: it is anchored
to the root organization whose prepaid corporate credit funds every card it
mints, so the existing ledger, RLS, and reconciliation machinery apply unchanged
and a compromised credential can never mint beyond the reseller's paid float.

### Payments module (`src/GiftCardPlatform.Modules.Payments/`)

| File | Contains |
| --- | --- |
| `Domain/PosClient.cs`, `PosTerminal.cs` | Platform-scoped POS integration and till identity, normalized codes, hashed secret, active/disabled lifecycle |
| `Domain/PosCredentialCodec.cs` | 256-bit client secret, SHA-256 hash at rest, constant-time verification that never throws on a malformed stored hash |
| `Application/PosRegistrationService.cs` | Platform-permission-gated client/terminal registration with audited, secret-free records |
| `Application/PosAuthenticationService.cs` | Credential exchange for a short-lived device token; unknown, disabled, and wrong credentials refused identically |
| `Infrastructure/PosConfigurations.cs` | Unique client code, per-client terminal code, hash-shape and disabled-coherence constraints |
| `Infrastructure/Migrations/*AddPosClientsAndTerminals*` | POS tables, deliberate absence of RLS with its reasoning, and identity-immutability triggers |
| `Domain/PaymentProvision.cs` | Active/Confirmed/Cancelled/Expired hold, ADR-044 two-minute window, immutable card display reference, confirmed amount and redemption transaction link, and ADR-054 `RequestedAmount`/`IsPartialApproval` with the invariant that a hold never exceeds what the till requested |
| `Domain/PaymentRefund.cs` | Immutable provision/redemption/card/POS attribution, normalized retry key/reason/reference, amount/currency, Ledger link, and timestamp |
| `Application/PaymentProvisionService.cs` | POS-only create/status/cancel/confirm/refund and bounded expiry; refund serializes its cap, revalidates lifecycle, posts inverse Ledger value, and audits atomically |
| `Application/PaymentReservationQuery.cs` | Active provisioned amount per card, consumed by Sharing; extracted so Sharing and Payments do not form a DI cycle |
| `Infrastructure/Migrations/*AddPaymentProvisions*`, `*AddPaymentTokenCandidatePolicy*`, `*AddRedemptionConfirmation*`, `*AddPaymentRefunds*`, `*AddPartialApprovalRequestedAmount*` | Provision/refund tables, forced RLS, safe backfill, append-only triggers, and serialized database over-refund backstop |
| `Domain/PaymentTokenCodec.cs` | 256-bit opaque CSPRNG credential `{tokenId:N}.{base64url secret}`, SHA-256 hex hashing, fixed-time verification; every parse failure clears both outputs |
| `Domain/NumericPaymentCodeCodec.cs` | CSPRNG 12-digit ASCII alias, separator normalization, SHA-256 hashing, and fixed-time verification (ADR-050) |
| `Domain/PaymentToken.cs` | Card/tenant/owner binding, issuance-derived expiry, single-use consumption stamp; no state column, expiry is clock-derived |
| `Application/PaymentTokenService.cs` | Exact-owner serializable issuance plus exact-token pending/active/terminal status; status never accepts or returns the raw/numeric credential, and issuance audit remains secret-free |
| `Infrastructure/PaymentsDbContext.cs`, `PaymentTokenConfiguration.cs` | `payments` schema, tenant/owner filter, hash-shape and expiry check constraints, card/expiry and owner indexes, `xmin` concurrency |
| `Infrastructure/Migrations/*InitialPayments*`, `*AddNumericPaymentCode*` | Token table, hash-only numeric alias, exact candidate RLS, unique hash, immutable identity/validity, and irreversible consumption |
| `PaymentsModuleExtensions.cs` | Validated 60-second TTL option, services, DbContext, and migration |

Public surface: `Modules.Payments.Contracts/PaymentContracts.cs` —
`IPaymentTokenService`, `IssuedPaymentTokenResult`, `PaymentTokenStatusResult`,
`PaymentTokenOptions`,
`IPaymentProvisionService`, `IPaymentProvisionExpirationProcessor`,
`IPaymentReservationQuery`, provision/refund request/result records, and
`PaymentProvisionOptions`. Ledger implements `IGiftCardPaymentLedger` (locked
card balance and idempotent redemption/refund posting); Sharing consumes
`IPaymentReservationQuery` so a share cannot
spend value a till already holds, and Payments consumes Sharing's reservation
query for the same reason in reverse.
Gift Cards implements `IGiftCardPaymentWriter`, whose `EnsureSpendable` check is
deliberately distinct from share eligibility: sharing also requires
transferable/divisible, which default to false and must not gate payment.

---

## 3. Constants and wire values

### Permissions

| Constant | Value |
| --- | --- |
| `PlatformPermissions.OrganizationsCreate` | `platform.organizations.create` |
| `PlatformPermissions.OrganizationsView` | `platform.organizations.view` |
| `PlatformPermissions.MembershipsView` | `platform.organizations.memberships.view` (read-only cross-tenant) |
| `PlatformPermissions.UsersCreate` / `UsersDisable` | `platform.users.create`, `platform.users.disable` |
| `PlatformPermissions.InitialAdministratorsAssign` | `platform.organizations.initial_administrators.assign` |
| `PlatformPermissions.CorporateCreditsAllocate` | `platform.corporate_credits.allocate` |
| `PlatformPermissions.CorporateCreditsView` | `platform.corporate_credits.view` |
| `PlatformPermissions.CorporateCreditsReverse` | `platform.corporate_credits.reverse` |
| `PlatformPermissions.GiftCardsView` / `GiftCardsManageLifecycle` | `platform.gift_cards.view`, `platform.gift_cards.lifecycle.manage` |
| `PlatformPermissions.AuditView` | `platform.audit.view` |
| `OrganizationPermissions.MembershipsCreate` | `organization.memberships.create` |
| `OrganizationPermissions.MembershipsView` | `organization.memberships.view` |
| `OrganizationPermissions.MembershipsDisable` | `organization.memberships.disable` |
| `OrganizationPermissions.CorporateCreditsView` | `organization.corporate_credits.view` |
| `OrganizationPermissions.GiftCardsIssue` / `GiftCardsView` | `organization.gift_cards.issue`, `organization.gift_cards.view` |
| `OrganizationPermissions.GiftCardsDistribute` | `organization.gift_cards.distribute` |
| `OrganizationPermissions.GiftCardsManageLifecycle` | `organization.gift_cards.lifecycle.manage` |
| `OrganizationPermissions.AuditView` | `organization.audit.view` |
| `OrganizationPermissions.View` | `organization.view` |
| `OrganizationPermissions.CreateSubsidiary` | `organization.create_subsidiary` |
| `PlatformPermissions.PosClientsManage` | `platform.pos.clients.manage` |
| `platform.partners.manage` | Register e-pin resellers and their API clients; disable either as the kill switch |
| `PlatformPermissions.PaymentsView` | `platform.payments.view` (read-only cross-tenant POS reporting) |
| `OrganizationPermissions.RoleView` / `RoleCreate` / `RoleAssign` / `RoleManagePermissions` | `role.view`, `role.create`, `role.assign`, `role.manage_permissions` |

### Authentication headers

| Header | Effect |
| --- | --- |
| `Authorization: Bearer <JWT>` | Signed access token; required by protected endpoints |
| `X-Organization-Id` | Requests an organization context; accepted only after active-membership verification |
| `X-Platform-Bootstrap-Secret` | One-time bootstrap credential; anonymous endpoint only, never persisted |

JWT bearer authentication is the only identity mechanism in Development,
Staging, and Production. Platform permissions and organization membership are
resolved from PostgreSQL after signature and lifetime validation.

### Endpoints

```text
GET  /api/v1/me                                                      authenticated identity and verified authority context
GET  /api/v1/me/organizations                                        exact-user active memberships; no selected organization
POST /api/v1/organizations                                            platform.organizations.create
GET  /api/v1/organizations                                            platform.organizations.view; root search/status/page
GET  /api/v1/organizations/{id}                                       platform.organizations.view
POST /api/v1/organizations/{organizationId}/memberships               organization.memberships.create
GET  /api/v1/organizations/{organizationId}/memberships               organization.memberships.view  | platform...memberships.view
POST /api/v1/organizations/{organizationId}/memberships/{id}/disable  organization.memberships.disable
POST /api/v1/organizations/{organizationId}/subsidiaries              organization.create_subsidiary
GET  /api/v1/organizations/{organizationId}/subsidiaries              organization.view
POST /api/v1/organizations/{organizationId}/roles                     role.create
GET  /api/v1/organizations/{organizationId}/roles                     role.view
POST /api/v1/organizations/{organizationId}/roles/{id}/permissions    role.manage_permissions
GET  /api/v1/organizations/{organizationId}/roles/assignments         role.view
POST /api/v1/organizations/{organizationId}/roles/assignments         role.assign
POST /api/v1/users                                                     platform.users.create
POST /api/v1/users/{id}/disable                                        platform.users.disable
POST /api/v1/auth/login                                                anonymous, rate-limited
POST /api/v1/auth/refresh                                              anonymous, one-time rotation
POST /api/v1/auth/revoke                                               anonymous, possession-based and idempotent
POST /api/v1/bootstrap/platform-administrator                          anonymous, secret-protected, one-time, rate-limited
POST /api/v1/organizations/{organizationId}/initial-administrator      platform.organizations.initial_administrators.assign
POST /api/v1/corporate-credits/allocations                             platform.corporate_credits.allocate
POST /api/v1/corporate-credits/allocations/{allocationId}/reversal     platform.corporate_credits.reverse
GET  /api/v1/organizations/{organizationId}/corporate-credits/balances platform.corporate_credits.view | organization.corporate_credits.view
GET  /api/v1/organizations/{organizationId}/corporate-credits/allocations platform.corporate_credits.view | organization.corporate_credits.view
POST /api/v1/organizations/{organizationId}/gift-cards/               organization.gift_cards.issue
GET  /api/v1/organizations/{organizationId}/gift-cards/inventory      organization.gift_cards.view
POST /api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/lifecycle/{suspend|reactivate|cancel|expire} organization.gift_cards.lifecycle.manage
GET  /api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/lifecycle/history organization.gift_cards.view
POST /api/v1/platform/gift-cards/{giftCardId}/lifecycle/{suspend|reactivate|cancel|expire} platform.gift_cards.lifecycle.manage
GET  /api/v1/platform/gift-cards/{giftCardId}/lifecycle/history        platform.gift_cards.view
POST /api/v1/me/gift-cards/{giftCardId}/lifecycle/{suspend|reactivate} exact identity owner
GET  /api/v1/me/gift-cards/{giftCardId}/lifecycle/history              exact identity owner
POST /api/v1/organizations/{organizationId}/gift-cards/{giftCardId}/distributions/ organization.gift_cards.distribute
POST /api/v1/gift-card-claims                                         directed or orphan PIN claim; anonymous/new identity or authenticated attach; rate/attempt limited
POST /api/v1/me/gift-cards/{giftCardId}/shares                        exact identity owner; raw link/PIN once
GET  /api/v1/me/shares                                                exact sender or recipient history
POST /api/v1/me/shares/{shareId}/cancel                               exact sender while pending
POST /api/v1/share-claims                                             authenticated existing recipient; PIN protected
POST /api/v1/me/gift-cards/{giftCardId}/share-invitations             exact identity owner; masked contact response, post-commit delivery
POST /api/v1/share-invitation-claims                                  anonymous, rate limited, verified contact; optional new-identity session
POST /api/v1/me/gift-cards/{giftCardId}/payment-tokens                exact identity owner; opaque 60s single-use credential returned once, no-store
GET  /api/v1/me/gift-cards/{giftCardId}/payment-tokens/{paymentTokenId} exact identity owner; credential-free pending/active/terminal checkout status, no-store
POST /api/v1/pos/clients                                              platform.pos.clients.manage; secret returned once, no-store
GET  /api/v1/pos/clients                                              platform.pos.clients.manage
POST /api/v1/pos/clients/{posClientId}/terminals                       platform.pos.clients.manage
GET  /api/v1/pos/clients/{posClientId}/terminals                       platform.pos.clients.manage
POST /api/v1/pos/auth/token                                           anonymous; POS client credentials plus terminal code, no-store
POST /api/v1/partners                                                 platform.partners.manage; anchors a reseller to an active root organization
GET  /api/v1/partners                                                 platform.partners.manage
POST /api/v1/partners/{partnerId}/clients                             platform.partners.manage; secret returned once, no-store
GET  /api/v1/partners/{partnerId}/clients                             platform.partners.manage; secrets never returned
POST /api/v1/partners/{partnerId}/clients/{clientId}/disable          platform.partners.manage; kill one key, effective next request
POST /api/v1/partners/{partnerId}/disable                             platform.partners.manage; kill a reseller, sold e-pins stay claimable
POST /api/v1/partners/auth/token                                      anonymous, rate-limited; uniform constant-time refusals, no-store
POST /api/v1/partners/gift-cards/mint                                 partner.gift_cards.mint; shared per-client quota; atomic card + no-store orphan link/PIN; tenant comes only from live principal
POST /api/v1/pos/payment-provisions                                   POS device token plus one payment credential; two-minute hold, no Ledger posting
GET  /api/v1/pos/payment-provisions/{provisionId}                     POS device token; only holds the calling client created
POST /api/v1/pos/payment-provisions/{provisionId}/cancel              POS device token; releases the hold, posts nothing
POST /api/v1/pos/payment-provisions/{provisionId}/confirm             POS device token; immutable redemption posting
POST /api/v1/pos/payment-provisions/{provisionId}/refunds             owning POS client; immutable capped partial refund
GET  /api/v1/me/shares/{shareId}/development-delivery                 exact sender, Development only
GET  /api/v1/development/organizations/{organizationId}/claim-deliveries/{invitationId} organization.gift_cards.distribute, Development only
GET  /api/v1/organizations/{organizationId}/reports/financial-summary corporate-credit view + gift-card view for organization or platform
GET  /api/v1/organizations/{organizationId}/reports/financial-history corporate-credit view + gift-card view for organization or platform
GET  /api/v1/organizations/{organizationId}/reports/reconciliation corporate-credit view + gift-card view for organization or platform
GET  /api/v1/organizations/{organizationId}/reports/card-register organization.gift_cards.view | platform.gift_cards.view; funded amount, masked recipient
GET  /api/v1/platform/reports/payments                                platform.payments.view; filter-bound payment/receipt totals
GET  /api/v1/platform/reports/payments/{paymentProvisionId}           platform.payments.view; receipt plus immutable refund lines
GET  /api/v1/me/gift-cards[/{giftCardId}[/history]]                    exact identity owner
GET  /api/v1/organizations/{organizationId}/audit-records             organization.audit.view | platform.audit.view
GET  /health          anonymous — liveness only, does not touch the database
GET  /health/ready    anonymous — readiness, 503 when PostgreSQL is unreachable
     /swagger, /demo  Development only
```

### PostgreSQL

| Item | Value |
| --- | --- |
| Schemas | `identity`, `organizations`, `audit`, `"authorization"`, `ledger`, `corporate_credits`, `gift_cards`, `distribution`, `sharing`, `payments`, `partners`. One DbContext + migration history each; Reporting deliberately owns none |
| POS tables | `payments.pos_clients`, `payments.pos_terminals`; platform-scoped so deliberately no RLS (same as platform roles), secret hash only, immutable identity/code/store, no runtime DELETE |
| Payments tables | `payments.payment_tokens`, `payments.payment_provisions`, `payments.payment_refunds`; SHA-256 QR/numeric hashes only, server-derived 60s expiry and two-minute hold, shared single-use consumption, confirmed/refund Ledger links, forced exact-candidate RLS, immutable payment/refund attribution, serialized refund cap, no runtime DELETE |
| Identity tables | `identity.users`, `identity.sessions`, `identity.refresh_tokens`; users have exactly one email/phone contact and refresh plaintext is never persisted |
| Authorization platform tables | `platform_roles`, `platform_role_permissions`, `platform_role_assignments`, `platform_bootstrap_state`; no tenant RLS |
| Initial-admin state | `authorization.organization_administrator_bootstraps` — one durable row per root organization |
| Ledger tables | `ledger.accounts`, `ledger.transactions`, `ledger.entries`; posted history is runtime insert/read-only |
| Corporate-credit tables | `corporate_credits.allocations`, `corporate_credits.reversals`; immutable one-to-one correction history |
| Gift-card tables | `gift_cards.gift_cards`, `gift_cards.lifecycle_events`; funding/issuing scope, organization/identity owner, lifecycle, ledger links, validity/policy/provenance, immutable actor/reason/return history |
| Distribution tables | `distribution.invitations`, `distribution.events`, `distribution.bulk_batches`, `distribution.bulk_items`; token hash only, immutable invitation identity/events, durable max-100 synchronous and max-2,000 async batches, persisted normalized row intent, one-way outcomes, counts, and retry lineage |
| Sharing tables | `sharing.shares`, `sharing.events`; token/PIN hashes only, protected/direct kind, RLS-protected normalized contact binding, immutable share identity/contact, append-only events, bounded state/attempt checks |
| Partner tables | `partners.partners`, `partners.api_clients`; secret hash only, tenant-owned with forced RLS keyed on the funding root, read-only `app.is_partner_credential_lookup` escape for anonymous credential exchange, immutable identity/tenant/code triggers, no runtime DELETE |
| Session settings | `app.user_id`, `app.organization_id`, `app.is_platform_operator`, exact `app.claim_invitation_id`, exact `app.share_id`, exact `app.payment_token_id`, `app.pos_client_id`; `app.is_initial_admin_bootstrap` only inside the controlled initial-admin transaction, and `app.is_partner_credential_lookup` only on the independent read-only connection that resolves a partner credential (all transaction-local). The verified tenant root is also held in `IExecutionContext` for root-keyed financial filters |
| RLS tables | Existing tenant tables plus Sharing shares/events use FORCE RLS; Gift Cards and Ledger add exact share-candidate policies for only the source/plan/child rows needed by `app.share_id`; all paths fail closed without verified transaction-local context |
| RLS helpers | `organizations.caller_root_organization_id()` resolves the root from `app.organization_id`; `organizations.organization_belongs_to_caller_tenant(uuid)` tests any operational organization against that root; `ledger.caller_owns_gift_card(uuid)` follows the RLS-protected current Gift Cards owner for read-only Ledger policies |
| Code uniqueness | `ux_organizations_root_code` (roots, global) and `ux_organizations_tenant_code` (`root_organization_id, code`, per tenant) — ADR-024 |
| RLS policy | Customer rows must belong to the caller's tenant root; named permissions still decide exact target scope. The sole tenant-authorization platform write exception is the initial-admin transaction with its transaction-local flag |
| Roles | `giftcard_migrator` (owns schemas/migrations), `giftcard_app` (runtime, `NOSUPERUSER NOBYPASSRLS`, no UPDATE/DELETE on `audit`) |
| Provisioning | `infra/postgres/init/01-roles-and-privileges.sh` (mirrored by `PlatformApiFixture.ProvisionAsync`) |

---

## 4. Tests

| Project | Notes |
| --- | --- |
| `tests/GiftCardPlatform.ArchitectureTests/ModuleBoundaryTests.cs` | `ModuleBoundaryTests` (cross-module refs, DbContext visibility) + `DomainPurityTests` (no EF/ASP.NET/Redis in `.Domain`) |
| `tests/GiftCardPlatform.ArchitectureTests/TokenCodecTests.cs` | Every `*Codec` declaring a static `TryParse` is discovered and must clear both outputs on every failure path, so a parsed identifier can never reach an RLS candidate after a refused parse |
| `tests/GiftCardPlatform.UnitTests/OrganizationTests.cs` | Code validation, ltree label/root-path helpers, root creation |
| `…/CredentialPolicyTests.cs` | Email normalization, composition-free Unicode password policy, blocklist and limits |
| `…/OrganizationSubsidiaryTests.cs` | Subsidiary path/depth computation, configurable depth limit |
| `tests/GiftCardPlatform.IntegrationTests/PlatformApiFixture.cs` | Harness: Testcontainers **or** external DB, provisions both roles, applies real migrations, uses ephemeral test-only Data Protection, and hosts `WebApplicationFactory`; background jobs are explicitly test-driven. Collection name `platform-api` |
| `…/DataProtectionConfigurationTests.cs`, `IsolatedApiFactory.cs` | Non-Development key-path gate, Development local default, notification payload recovery across provider restarts, and isolated durable key rings for standalone environment probes |
| `…/MembershipTestSupport.cs` | Client builders for both caller kinds, `SetSessionContextAsync`, `CountMembershipsAsync` |
| `…/MembershipTenantIsolationTests.cs` | RLS proofs via raw SQL with the app filter deliberately absent |
| `…/SubsidiaryTests.cs` | Subsidiary creation, cross-tenant rejection, depth limit, atomicity, listing |
| `…/OrganizationTenantIsolationTests.cs` | RLS proofs for the organizations table (ADR-023) |
| `…/TransactionCoordinatorTests.cs` | Nesting, isolation levels, serialization failure (ADR-026) |
| `…/DenialAuditTests.cs` | Refused operations are recorded and survive rollback (ADR-025) |
| `…/AuditTenantIsolationTests.cs` | Forced audit RLS, fail-closed context-free reads, customer isolation, and controlled platform visibility |
| `…/AuditCheckpointTests.cs` | Real-PostgreSQL signed/witnessed pipeline, writer/sealer boundary, tamper/missing evidence detection, outage isolation, and runtime privileges |
| `…/PaginationTests.cs` | Paged list endpoints, plus `ConcurrencyTokenTests` for `xmin` |
| `…/ScopedSqlSession.cs` | Raw SQL with an RLS session context — required now that both tables are behind RLS |
| `…/MembershipTests.cs`, `OrganizationCreationTests.cs`, `AtomicAuditTests.cs`, `SessionContextTests.cs`, `DemoAvailabilityTests.cs` | Behaviour, atomicity, append-only privileges, session context |
| `…/IdentityAuthenticationTests.cs` | User lifecycle, hashing, JWT/refresh lifetimes, rotation/reuse/concurrency, expiry/revocation, bearer membership, rate limiting, secret-free audit |
| `…/PlatformBootstrapTests.cs` | One-time/concurrent platform bootstrap, persisted JWT permissions, initial Company Administrator assignment, root/user validation, subtree authority, and secret-free audit |
| `…/DemoAvailabilityTests.cs` | Complete Phase 2 console content including send/claim/lifecycle, finance/reconciliation, and My Cards controls; Development-only availability, JWT-only authentication, and bearer registration in all environments |
| `…/MoneyTests.cs`, `LedgerTransactionTests.cs`, `CorporateCreditIntentTests.cs` | Phase 2 money validation, balancing, account-scope, value-return directionality, and idempotent-intent rules |
| `…/CorporateCreditAllocationTests.cs` | Allocation authorization/eligibility, atomic ledger/audit writes, idempotency, concurrency, immutable privileges, and financial RLS |
| `…/GiftCardIssuanceTests.cs` | Funded issuance, defaults, permission/subtree scope, idempotency, overspend concurrency, inventory paging, tenant/identity-owner RLS, and provenance privileges |
| `tests/GiftCardPlatform.UnitTests/GiftCardIssuanceTests.cs` | Issuance normalization/defaults, validity, ownership/lifecycle, provenance, and matching intent |
| `…/GiftCardDistributionTests.cs` | Email/phone activation, new-identity sessions, existing-identity login boundary, claim/session rollback and replay, trusted/untrusted proxy rate partitions, OpenAPI, no membership/ledger movement, permission, concurrency, forced RLS, and database immutability |
| `tests/GiftCardPlatform.UnitTests/DistributionDomainTests.cs` | Contact/token validation, hashing, expiry, attempt bound, invitation state, and claim idempotency |
| `…/BulkGiftCardBatchTests.cs` (Integration) | Synchronous atomic regression plus 1,500-row durable async acceptance, restart-safe mixed outcomes, child retry, masked paging, distinct concurrent claims, exact card/Ledger/outbox effects, permissions, forced RLS, and database immutability |
| `tests/GiftCardPlatform.UnitTests/BulkGiftCardBatchTests.cs` | Ordered intent normalization/hash/child keys, 100/2,000 bounds, durable pending intent, one-way mixed settlement, failed-only child retry, safe item errors, mapping, and totals |
| `…/GiftCardLifecycleTests.cs` (Integration) | Company/platform/owner/system authority, idempotency, exact returns, invitation closure, paused activation, expiration, RLS/terminal immutability, and claim/cancel race |
| `tests/GiftCardPlatform.UnitTests/GiftCardLifecycleTests.cs` | Lifecycle intent normalization, ownership-derived transitions, terminal/validity rules, and immutable terminal-event financial attribution |
| `…/ReportingTests.cs` (Integration) | Exact totals/timelines, authoritative literal and UTC-bounded search, filter-bound pagination, OpenAPI parameters, clean and orphan reconciliation, permissions, audit filters, exact-owner RLS, historical visibility, and fail-closed boundaries |
| `…/FrontendClientDiscoveryTests.cs` (Integration) | Current identity/platform/organization context, exact-user picker, platform root search/status/page, OpenAPI, anonymous/permission denials, and raw SELECT-only RLS |
| `…/ClientContractConvergenceTests.cs` (Integration) | One served OpenAPI document containing both cardholder claim-session and portal team-administration contracts |
| `tests/GiftCardPlatform.UnitTests/FinancialHistorySearchFilterTests.cs` | Filter normalization, literal pattern escaping, UTC conversion/range validation, and equivalent-filter fingerprint coverage |
| `tests/GiftCardPlatform.UnitTests/ReportingCursorTests.cs` | Unfiltered and filter-bound cursor round-trip, version/fingerprint isolation, strict shape/bounds validation, and malformed-input rejection |

Use random (v4) GUIDs for generated unique codes — a UUID v7's leading hex is a
millisecond timestamp and collides within the same millisecond.

| `tests/GiftCardPlatform.UnitTests/SharingDomainTests.cs`, `RecipientContactServiceTests.cs` | Protected token/PIN hashing, direct kind/contact state, Identity-owned normalization/masking, fifth-attempt lock, claim state, and idempotency |
| `tests/GiftCardPlatform.IntegrationTests/GiftCardSharingTests.cs` | Generic and direct new/existing-recipient create/claim/replay, balanced posting, child lineage, reserved/available reporting, cancel/lock/source closure, concurrent retry, RLS, immutability, masked audit/API, hashes-at-rest, demo, and OpenAPI |
| `tests/GiftCardPlatform.UnitTests/PaymentTokenTests.cs` | Opaque token round-trip plus numeric generation/normalization/fixed-time hashing, malformed-input rejection, expiry boundary, and issuance validation |
| `tests/GiftCardPlatform.IntegrationTests/PosAuthenticationTests.cs` | Secret returned once and hashed at rest, permission gate, device-token issuance, indistinguishable refusals, POS token barred from cardholder/organization scope, and OpenAPI |
| `tests/GiftCardPlatform.IntegrationTests/PaymentTokenIssuanceTests.cs` | Exact-owner dual issuance, 60-second TTL, `no-store`, distinctness, QR/numeric hash-only persistence and secret-free audit, stranger 404 vs suspended 409, exact numeric-candidate RLS, and issuance/status OpenAPI secrecy |
| `tests/GiftCardPlatform.IntegrationTests/PaymentProvisionTests.cs` | Exact-owner pending/active/confirmed status and cross-owner hiding; provision/confirmation/refund safety plus platform reporting totals, filters, receipt lines, and OpenAPI secrecy |
| `tests/GiftCardPlatform.UnitTests/PaymentProvisionTests.cs` | Window derivation, state transitions, terminal settlement, and holding-at-instant boundaries |
| `tests/GiftCardPlatform.IntegrationTests/OrganizationCardRegisterTests.cs` | Register lists a claimed card inventory no longer shows, withholds its balance while reporting an inventory card's, masks the recipient, and proves the permission, cross-tenant, filter, and cursor boundaries |
| `tests/GiftCardPlatform.UnitTests/OrganizationCardRegisterFilterTests.cs` | Closed-set state matching, currency shape, literal wildcard escaping, and filter-bound cursor rejection |
| `tests/GiftCardPlatform.UnitTests/PaymentReportingSearchFiltersTests.cs` | POS report case/UTC normalization, literal wildcard escaping, fingerprint isolation, and invalid state/currency/range/identifier rejection |

---

## 5. Recipes

**Add an audited operation**

```csharp
await using var tx = await transactionCoordinator.BeginAsync(ct);  // writes session context
await tx.EnlistAsync(dbContext, ct);
// ... domain write ...
await auditRecorder.RecordAsync(new AuditEntry(...), ct);          // same transaction
await tx.CommitAsync(ct);                                          // dispose w/o commit ⇒ rollback
```

Reads also open a transaction, so the RLS session context is established before
the query runs.

**Add a new tenant-owned table** — record or resolve both its customer tenant
root and operational organization; add `ENABLE` + `FORCE ROW LEVEL SECURITY`
and a policy in the migration; mirror the root boundary through
`TenantDbFunctions` (or the verified root for root-keyed financial rows); keep
exact target permission checks in the application service; add raw-SQL isolation
tests with the EF filter absent.

**Commands**

```bash
dotnet build
dotnet test tests/GiftCardPlatform.IntegrationTests/GiftCardPlatform.IntegrationTests.csproj
dotnet ef migrations add <Name> --project src/GiftCardPlatform.Modules.<M> \
  --startup-project src/GiftCardPlatform.Api --context <M>DbContext \
  --output-dir Infrastructure/Migrations
```

Integration tests need real PostgreSQL: Docker (default) or
`GIFTCARD_TEST_CONNECTION` pointed at an admin-capable database whose name
contains `test`.

---

## 6. Authorization model (IMPL-004)

```text
Role (owned by one organization, no scope of its own)
  └── RolePermission          granted permission names
MembershipRoleAssignment      membership + role + ScopeType + AnchorOrganizationId
  └── MembershipRoleAssignmentScope   explicit organizations (SelectedOrganizations only)
```

Effective permissions = union over every assignment whose scope covers the target
organization. **Parent-organization ownership alone grants nothing.**

| Scope | Covers |
| --- | --- |
| `Organization` | the anchor only |
| `Subtree` | the anchor and all descendants, via `IOrganizationHierarchyQuery` (ltree path) |
| `SelectedOrganizations` | exactly the listed organizations |

Guardrails enforced in `RoleService`: a platform permission cannot be granted to
an organization role; a caller cannot grant a permission it does not itself hold;
a role from another organization is invisible (404, never a confirming 403);
unknown permission names are rejected by a foreign key to the catalogue.

**Enforcement (IMPL-005):** organization application services use
`IOrganizationPermissionAuthorizer`, which evaluates the verified
`ActiveMembershipId` through `IPermissionEvaluator` inside the operation
transaction. No caller-supplied identity or permission header exists; signed JWT
identity and persisted assignments are mandatory.
