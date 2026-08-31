# Frontend Integration Contract

## Purpose

This is the handoff contract for the independent finance/customer portal and
recipient gift-card application. This backend remains authoritative for
identity, authorization, tenant isolation, ownership, and financial state.
Frontend code may improve navigation and hide unavailable actions, but it must
never be treated as an authorization boundary.

The durable architecture decision is ADR-037 in `docs/DECISIONS.md`.

---

## Repository Boundaries

| Repository | Responsibility |
| --- | --- |
| Backend (this repository) | Versioned API, identity, permission evaluation, RLS, business rules, Ledger, audit |
| Finance/customer portal | Platform and company staff workflows, finance-oriented presentation, BFF/browser session |
| Recipient app | Recipient login, owned-card experience, lifecycle, and later sharing/payment presentation |

The Development `/demo` page is a technical workflow console. It remains useful
for backend verification but is not the visual or usability baseline for either
production client.

---

## Browser and Native Security Boundary

The backend accepts JWT bearer access tokens and rotating refresh tokens.

For a production browser:

1. Put a same-origin BFF or trusted reverse proxy in front of the backend.
2. The browser sends credentials to the BFF over HTTPS.
3. The BFF exchanges credentials with `/api/v1/auth/login`, stores/rotates the
   token pair server-side, and returns only an `HttpOnly`, `Secure`,
   appropriate-`SameSite` session cookie.
4. The BFF adds CSRF protection to state-changing browser requests.
5. The BFF forwards API calls with the short-lived access token and, when
   selected, `X-Organization-Id`.

Do not put refresh tokens in `localStorage`, `sessionStorage`, URL parameters,
logs, analytics, or browser-readable cookies. Do not enable broad API CORS as a
shortcut around the BFF boundary.

A native mobile package may call the bearer API directly only when refresh
credentials are stored in operating-system secure storage.

### Recipient activation through the BFF

`POST /api/v1/gift-card-claims` has an optional `session` response object. It is
present only when that claim created the recipient identity. The BFF must
consume the access and refresh tokens server-side, establish its own secure
browser session, and return no backend token to browser JavaScript. The response
continues to expose only `maskedLoginIdentifier`.

When claim reuses an existing identity, `session` is `null`. The recipient must
use normal password login; possession of an activation link is not authority to
authenticate that account or read its other cards. A completed claim that
originally created the identity may return a fresh session on replay only when
the supplied password is verified.

For correct source-IP rate limiting behind a BFF:

1. Configure the immediate BFF address as a literal
   `Networking:ForwardedHeaders:KnownProxies` entry on the backend.
2. Derive the client address from the BFF's observed network connection.
3. Overwrite the outbound `X-Forwarded-For` header with that one address.
4. Never append, preserve, or trust a browser-supplied forwarding header.

The backend accepts one forwarded hop only from an allowlisted immediate proxy.
Without that configuration, it deliberately partitions quotas by the direct
remote address.

### Protected sharing through the cardholder BFF

IMPL-022 adds the authenticated protected-link contract:

* `POST /api/v1/me/gift-cards/{giftCardId}/shares` creates a reservation and
  returns `claimUrl` plus a six-digit `pin` once with `Cache-Control: no-store`;
* `GET /api/v1/me/shares` returns sent and received share history and accepts
  optional exact `kind`, `state`, and `direction` filters. A returned cursor is
  bound to that filter set and must not be reused with different filters;
* `POST /api/v1/me/shares/{shareId}/cancel` cancels a pending sent share;
* `POST /api/v1/share-claims` requires an authenticated existing recipient and
  atomically returns the new child card after token/PIN verification.

The browser selects a card from `GET /api/v1/me/gift-cards`; it must never ask a
user to paste a card/share UUID. Owned-card summary/detail responses expose
backend-authored `balance` (posted), `reservedBalance`, and `availableBalance`.
Clients must not recalculate availability or decide eligibility locally.

The BFF may carry the opaque claim token from an incoming HTTPS link into a
short-lived server-side flow and should remove it from the visible URL after
intake. Do not write the claim token or PIN to browser storage, logs, analytics,
telemetry, or error pages. Creation UI must explain that the link and PIN should
be copied through separate trusted channels. Claim is a CSRF-protected POST;
GET/link preview traffic never consumes value. Five wrong PIN attempts lock the
share and cancellation/expiry release the reservation without moving posted
Ledger value.

IMPL-023 adds the contact-bound path:

* `POST /api/v1/me/gift-cards/{giftCardId}/share-invitations` accepts amount,
  email/phone contact, and idempotency key; it returns only masked contact and
  commits before notification delivery. `deliveryDispatchedThisRequest` is true
  only for the request that created and dispatched the invitation; an
  idempotent replay returns the same share without reissuing raw credentials;
* the notification link is consumed server-side by the cardholder BFF and
  `POST /api/v1/share-invitation-claims` submits token, optional new-identity
  password, and idempotency key without requiring an existing session;
* a new contact returns `session`, which the BFF must consume directly into its
  normal HttpOnly browser session; an existing contact returns `session: null`
  and continues through normal login with the masked identifier as a hint.

The browser must never receive the unmasked invitation contact from a backend
response or persist the token/password/session tokens in JavaScript storage.
After server-side token intake, redirect to a clean URL; activation remains a
CSRF-protected POST and GET/link preview traffic never claims value. The finance
portal receives no share command authority.

IMPL-024 adds read-only Phase 3 detail without moving policy into a client:

* share rows include nullable `sourceGiftCardPublicReference` and
  `childGiftCardPublicReference` only when the signed-in identity can see that
  card through Gift Cards RLS; UUIDs remain navigation keys, never user input;
* organization financial history includes `category=Sharing` lifecycle rows,
  public card references, reservation/transfer/release direction labels, and
  only masked direct-recipient contact;
* reconciliation adds `sharesChecked` and `activeReservationsChecked` and may
  return deterministic sharing reservation, transfer, or lineage findings.

Frontends render these backend-authored values and finding messages. They must
not recalculate issuance, reservations, transfer balance, or lineage, and must
not offer a repair command.

---

## Session and Context Flow

### 1. Login

```http
POST /api/v1/auth/login
Content-Type: application/json

{
  "email": "staff@example.com",
  "phoneNumber": null,
  "password": "..."
}
```

A recipient may instead provide the claimed E.164 phone number. Exactly one
identifier is used. Normal login and refresh do not send email or SMS.

### 2. Resolve the current authority

Call without `X-Organization-Id`:

```http
GET /api/v1/me
Authorization: Bearer <access-token>
```

`contextType` is:

* `Identity` — authenticated user with no platform authority selected;
* `Platform` — persisted platform permissions were resolved;
* `Organization` — an active membership and tenant root were verified for the
  supplied organization header.

Never derive permissions by decoding a token in the UI. Use the response, and
still expect the server to deny stale or unauthorized operations.

### 3. Populate the organization picker

Call without `X-Organization-Id`:

```http
GET /api/v1/me/organizations?limit=50&offset=0
Authorization: Bearer <access-token>
```

Only the current user's active memberships are returned. `hasMore` determines
whether to request the next offset. A picker result is navigation data, not an
authorization grant.

### 4. Select and verify an organization

After the user selects an organization, make a new request:

```http
GET /api/v1/me
Authorization: Bearer <access-token>
X-Organization-Id: <selected-organization-id>
```

Authentication re-verifies the exact active membership and tenant root. A
successful `Organization` response contains:

* `membershipId`;
* `tenantRootOrganizationId`;
* the selected organization;
* effective permissions evaluated against that exact target.

Use this response to build organization navigation. If it fails, clear the
selection and return to the picker.

### 5. Platform customer directory

A platform operator with `platform.organizations.view` may call:

```http
GET /api/v1/organizations?search=acme&status=Active&limit=50&offset=0
Authorization: Bearer <access-token>
```

Search is a literal, case-insensitive name/code substring. Status is exactly
`Active`, `Suspended`, or `Disabled`. Only root customer organizations are
listed.

---

## Phase 4 Payment Presentation Contract

An authenticated exact cardholder requests a fresh credential with:

```http
POST /api/v1/me/gift-cards/{giftCardId}/payment-tokens
Authorization: Bearer <access-token>
```

The `no-store` response contains `rawToken`, `numericCode`, `issuedAtUtc`, and
`expiresAtUtc`. `rawToken` is the value encoded in the QR image. Render the
12-digit `numericCode` as three groups of four for manual entry without changing
its value. Both forms identify one server record and become unusable together
when either is consumed or the 60-second window ends. Do not persist, log,
cache, or send either value to analytics.

An authenticated POS submits exactly one of the additive request fields to
`POST /api/v1/pos/payment-provisions`: `paymentToken` for a scan or
`paymentCode` for manual entry. Numeric input may contain spaces or hyphens;
the backend remains the authority for normalization, expiry, replay, card
eligibility, available value, and financial effects.

---

## Response Shapes

The exact generated schema is authoritative at
`/swagger/v1/swagger.json` in Development. The discovery envelopes have these
stable shapes:

```json
{
  "id": "uuid",
  "email": "staff@example.com",
  "phoneNumber": null,
  "status": "Active",
  "contextType": "Organization",
  "platformPermissions": [],
  "organizationContext": {
    "membershipId": "uuid",
    "tenantRootOrganizationId": "uuid",
    "organization": {
      "id": "uuid",
      "name": "Customer",
      "code": "CUSTOMER",
      "status": "Active",
      "depth": 0,
      "createdAtUtc": "2026-07-28T00:00:00Z"
    },
    "effectivePermissions": [
      "organization.gift_cards.view"
    ]
  }
}
```

List operations return:

```json
{
  "items": [],
  "limit": 50,
  "offset": 0,
  "hasMore": false
}
```

Clients should preserve the server's Problem Details `code` for diagnostics and
map known codes to audience-appropriate messages. They must not display stack
traces, tokens, or raw unexpected server responses.

---

## Client Implementation Checklist

* Generate or type the client from Development OpenAPI; do not copy backend
  entities into a second business model.
* Centralize the selected organization and header injection.
* On `401`, attempt at most one server-side refresh, then end the session.
* On `403`, keep the user signed in but explain that the action is unavailable.
* On organization-context `400`/`404`, clear stale selection and reload the
  exact-user picker.
* Respect `limit`, `offset`, `hasMore`, opaque cursors, idempotency keys, and
  correlation identifiers documented by each operation.
* Treat money as currency plus decimal value; never convert it through
  floating-point arithmetic.
* Never log credentials, bearer tokens, refresh tokens, activation secrets, or
  unmasked recipient contact data.
* Treat a claim `session` as BFF-only credential material; never serialize it
  into browser state, URLs, client logs, or analytics.
