# POS strategy: research and architecture report

Written 2026-08-19. No code was changed to produce this. Findings about our own
system come from reading the implementation, not the READMEs; each is cited by
file and line so it can be checked.

---

## 0. The gate you set: are the backend and portal close to a 1.0-quality release?

**No, and I would not make the POS the next thing we build.** But the research
below changed my view of *why*, and it is not the reason either of us expected.

What is genuinely strong: 239 unit, 15 architecture and 361 real-PostgreSQL
integration tests; architecture rules enforced from build output rather than a
hand-kept list; RLS as the authoritative isolation barrier; a posted-only
double-entry ledger; CI and CodeQL green on all four repositories as of today.

What keeps it from 1.0:

1. **Nothing has ever been deployed, and the cardholder UI has never been
   opened by a person.** Every trap in the handoff came from something never
   executed.
2. **No release exists.** Zero tags, zero releases publicly, while the backend
   README announces `v0.4.0-rc.2` and `SECURITY.md` says the published tags
   should not be used. Two published documents contradict each other.
3. **Config drift** between clients (`:5144` vs `:5143`,
   `DataProtection:KeysPath` vs `KeyPath`).
4. **Deliberate gaps already documented**: Data Protection keys unpersisted
   outside Development, audit checkpoint signing with no managed key custody, a
   process-local rate limiter, no CORS story.

And then the finding that actually matters here:

5. **The payments core cannot do what real gift card acceptance requires.** It
   refuses any payment larger than the card's available value
   (`PaymentProvisionService.cs:161`), and there is no way for a till to ask
   what a card is worth. This is a backend 1.0 gap, not a POS gap, and I only
   found it by researching the POS question. See §2.

So the sequencing I recommend is not "finish 1.0, then do POS." It is: **decide
the POS product boundary now (this document), fix the two payments gaps it
exposes as part of backend 1.0, and build the POS client after.**

---

## 1. Industry research

### 1.1 The dominant architecture is "semi-integrated", and it is close to what you proposed

Retail payments split into three architectures:

- **Non-integrated.** A standalone terminal beside the till. The cashier reads
  the total off the till screen and keys it into the terminal. No data flows
  between them.
- **Semi-integrated.** The till sends the amount to a separate payment
  component, which owns the sensitive part of the flow and returns a
  non-sensitive result. The till never touches card data.
- **Fully integrated.** The till itself handles the payment data end to end.

Semi-integrated is the industry norm for new builds, and the reason is
compliance scope: keeping the cash register and back office out of the
transaction flow reduces PCI DSS scope, and sensitive data is encrypted inside
the payment component and sent directly to the processor, so the POS software
never sees it ([Ingenico](https://ingenico.com/us-en/newsroom/blogs/why-a-semi-integrated-payment-architecture-might-work-for-your-business),
[North](https://developer.north.com/blog/payments-architecture)).

**Your proposed workflow is the semi-integrated pattern.** That instinct is
correct and matches where the industry has landed. The problem is not the shape;
it is two specific details, covered in §1.4 and §2.

The same separation is formalised in the **nexo Retailer Protocol**, an ISO
20022 based standard defining the interface between the electronic cash register
and the payment application, whose stated design principle is "a clear
separation between sale and payment"
([nexo standards](https://www.nexo-standards.org/standards/nexo-retailer-protocol)).
That phrase is the product boundary we should adopt, and I use it in §4.

### 1.2 How the amount actually gets from the till to the payment component

Not by retyping. The mainstream mechanism is a **local network API call**.

Adyen's Terminal API is the clearest published example: the POS application
makes an API request directly to the IP address of the terminal, which listens
for POST requests on `/nexo` at port 8443, exchanging JSON messages, with the
response returned synchronously; local communications are protected by pinning
the vendor certificate into the POS app
([Adyen local integration](https://docs.adyen.com/point-of-sale/design-your-integration/choose-your-architecture/local),
[Terminal API](https://docs.adyen.com/point-of-sale/design-your-integration/terminal-api)).

Two things follow for us:

- The interface a till expects is **a small synchronous request/response API on
  the local network**, not a library it must link, and not a human retyping a
  number.
- JSON over HTTP is an accepted shape for this. Adyen explicitly notes that JSON
  messages need no libraries, which is what makes integration cheap.

### 1.3 Gift cards specifically: balance inquiry and split tender are table stakes

Gift card acceptance is not simply "a card that pays". The lifecycle a POS is
expected to drive covers issuance, value loading, activation, balance inquiries,
partial redemptions, and reconciliation, and a POS must communicate with the
gift card platform for activation, balance inquiry, and redemption
([Stripe](https://stripe.com/en-mx/resources/more/gift-card-processing)).

Two behaviours are near-universal at the till:

- **Balance inquiry.** The cashier runs an inquiry by swiping or entering the
  card, and the remaining balance is displayed and printed
  ([retailcloud](https://retailcloud.zendesk.com/hc/en-us/articles/360046999254-Tenders)).
- **Split tender.** A sale is settled with more than one tender, commonly a gift
  card plus another method, and POS products support splitting a sale across
  several tenders
  ([Lightspeed](https://shopkeep-support.lightspeedhq.com/hc/en-us/articles/47480028156315-Split-tender-transactions),
  [giftcards.com](https://www.giftcards.com/us/en/blog/split-tender-transactions)).

There is an important distinction the industry draws that we currently collapse:
a **split tender** is planned, where the customer intends from the start to use
several methods, while a **partial approval** is reactive, where the card is
declined for the full amount and only a partial charge is approved
([PosPayNews](https://www.pospaynews.com/applications/retail/split-payments-retail-pos-a-complete-guide.html)).

For gift cards the reactive case is the common one, because the customer usually
does not know the exact remaining balance. This is precisely the case our
backend refuses.

### 1.4 USB QR/barcode scanners behave as keyboards, which is both easy and dangerous

A USB scanner in the default mode is a **keyboard wedge**: it appears to the
operating system as a keyboard and injects the decoded payload as if typed, with
a configurable suffix, most often Enter (CR, `0x0D`), sometimes Tab
([Zebra](https://docs.zebra.com/us/en/scanners/general/sm72-ig/keyboard-wedge-interface.html),
[HID keyboard wedge guide](https://barcodescanneremulator.dev/guides/hid-keyboard-wedge)).
Windows also supports a distinct **HID POS Scanner** mode handled by a dedicated
driver implementing the HID Point of Sale usage tables, which is not keyboard
input ([Microsoft](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/pos/barcodescanner-configure)).

Consequences for design:

- Reading a scan needs **no driver and no SDK**: a focused text input plus an
  Enter-terminated payload is enough. This is why the "just add a scanner"
  assumption in your workflow is sound.
- But keyboard wedge input goes **wherever focus is**. If the cashier's focus is
  in the till application, the card payload is typed into the till, possibly
  into a product search box, and possibly into its logs. A payment credential
  landing in another application's log is a real security problem, not a
  cosmetic one.
- Scanner misconfiguration (wrong keyboard layout) silently corrupts payloads,
  a well documented failure mode. Our credential format should be robust to it
  or detect it, rather than producing an opaque "invalid code".

### 1.5 Standalone companion apps: common, but as the fallback tier

Non-integrated standalone terminals remain widespread in small business, and are
the normal starting tier. They are accepted because they are cheap and require
no integration work, and their known cost is exactly the one your workflow has:
the cashier re-keys the amount and then manually records the tender back in the
till, which is slow and error-prone and leaves reconciliation to humans.

The pattern to copy is therefore **tiered**, not either/or: a standalone mode for
shops that cannot integrate, and a local API for those that can, with the same
core doing the work.

---

## 2. Current-state assessment

Read from the implementation. File and line references are to `open-giftcard`.

### 2.1 What the backend already provides, and it is a lot

The payment model is a textbook **authorize / capture / void / refund** cycle,
which is the correct primitive set and maps directly onto card-scheme thinking:

| Operation | Endpoint | Behaviour in code |
| --- | --- | --- |
| Credential issue | `POST /v1/me/gift-cards/{id}/payment-tokens` | 256-bit opaque credential plus a numeric code, single use, 60-second TTL |
| Authorize / hold | `POST /v1/pos/payment-provisions` | consumes the credential exactly once, holds an amount, posts nothing to the ledger |
| Read | `GET /v1/pos/payment-provisions/{id}` | hold state |
| Void | `POST .../cancel` | releases the hold, posts nothing |
| Capture | `POST .../confirm` | charges a stated amount **up to the held ceiling**, releases the remainder, posts the balanced ledger redemption |
| Refund | `POST .../refunds` | multiple immutable partial refunds, idempotency key, `RemainingRefundableAmount` returned |

Specific things already right that most first attempts get wrong:

- **Two credential presentations.** `CreatePaymentProvisionRequest` accepts
  either `PaymentToken` or `PaymentCode`
  (`PaymentContracts.cs`), so a scan and a manual keypad fallback are both
  supported. This is exactly what a till needs when a screen is cracked or a
  phone will not brighten.
- **The credential carries nothing.** It is resolved by server-side lookup and
  carries no card, owner, amount, or balance (`PaymentEndpoints.cs:19-21`), and
  unknown, expired, consumed and replayed credentials are refused identically
  and in constant time. That is a deliberate anti-oracle design.
- **Holds are visible to other spending paths.** `IPaymentReservationQuery`
  exists so sharing cannot spend value already promised to a till, and vice
  versa (ADR-033). Double-spend across features was thought about.
- **Hold expiry is bounded and enforced.** `PaymentProvisionOptions.WindowSeconds`
  is 120, validated at startup so an environment cannot silently widen how long
  an abandoned till holds a cardholder's money, with a background expiration
  processor.
- **Receipt correlation already exists.** `PosTransactionReference` is carried
  on the provision and on each refund, and `StoreReference` is carried on the
  terminal.
- **Machine identity is modelled properly.** `POST /v1/pos/auth/token` exchanges
  a client code and secret for a signed device token
  (`PosAuthenticationService.cs`), the secret is returned once and only its hash
  is stored (ADR-043), terminals are registered per client and carry a store
  reference, and disable switches exist.

**This is a better foundation for POS integration than I expected, and better
than several commercial gift card APIs.** The vocabulary maps cleanly onto real
systems: client is roughly a merchant integration, terminal is a lane, store
reference is a site, provision is an authorization, confirm is a capture,
`PosTransactionReference` is the ECR receipt correlation field.

### 2.2 The two gaps that block real-world use

**Gap 1: no partial approval, and no split tender.**
`PaymentProvisionService.cs:160-165`:

```
var available = balance.Amount - shared - provisioned;
if (request.Amount > available)
    // payment.provision.insufficient_value
```

If the basket is 50 and the card holds 30, the till gets a refusal. In real
retail this is the *normal* case, and the expected behaviour is a partial
approval for 30 with 20 left owing to another tender (§1.3). As written, a
cashier can only succeed by guessing an amount at or below a balance nobody has
told them.

**Gap 2: no balance inquiry for a till.** There is no POS-facing endpoint that
answers "what is this card worth". The cardholder can see it; the till cannot.
Combined with Gap 1 this is a dead end: the till must know the balance to choose
an amount, and cannot ask.

There is a real design tension here, and it is why I am not proposing a naive
fix. An unauthenticated or freely repeatable balance endpoint is a card
enumeration and balance harvesting oracle, which is exactly what the constant
time uniform refusal design is protecting against. The resolution used in
industry is that inquiry requires the card to be **presented**, and is
rate-limited and audited like any other transaction.

### 2.3 Smaller gaps

- **A create timeout orphans value.** If `POST /pos/payment-provisions` succeeds
  server-side but the response is lost, the credential is consumed and the till
  never learns the provision id. It cannot cancel what it cannot name. The
  2-minute expiry bounds the damage but the customer sees their money missing
  meanwhile. `CreatePaymentProvisionRequest` has no idempotency key, while
  refunds do. This is the single most common failure in real till integrations
  and it should be closed.
- **No offline capability, anywhere.** Correct for now, and I am not proposing
  store-and-forward: for stored value it means authorising against a balance you
  cannot see, and the loss lands on the merchant or the platform. But it must be
  stated as a supported limitation, because supermarkets will ask.
- **No activation or load at the till.** The lifecycle industry expects includes
  activating and topping up a card at the counter (§1.3). We have neither at POS.
  This may be deliberate scope, but it should be an explicit decision.
- **The POS repository itself is behind.** Verified today: no security-headers
  middleware, no `/health/ready`, no Data Protection key enforcement outside
  Development, and **no contract pin at all** (no `contracts/` directory), so it
  is the only client whose backend coupling CI does not guard.

### 2.4 Deployment-specific or demo-specific assumptions

The backend is clean: no branding in namespaces, types, schema, or seed data.
What remains is in the POS client and the demo data, not the platform: the
demo till registration and store reference, and formerly a retailer-specific
basket and currency default, now replaced. The `MockCart` in the POS is
demonstration scaffolding, not a product feature, and should not survive into
whatever we build next.

### 2.5 Is the current API suitable for third-party till integration?

**Yes, with the two gaps closed.** It is REST, JSON, versioned, documented via
OpenAPI with a pinned contract snapshot, uses bearer device tokens rather than
sessions, has no CORS dependency, refuses ambiguously on purpose, and models
idempotency where it matters most. A competent till vendor could integrate
against it. The blockers are behavioural, not structural.

---

## 3. Integration models compared

| Model | What we ship | Pros | Cons | Fits |
| --- | --- | --- | --- | --- |
| **1. Standalone POS app** | A cashier UI, run beside the till | No integration work; works with any till; demo-able | Cashier re-keys the amount and re-keys the result back into the till; reconciliation is manual; two screens | Small shops, market stalls, pilots |
| **2. SDK / library** | Packages per language | Type-safe; feels native | We must maintain N languages; every till stack we do not ship is excluded; a solo maintainer cannot sustain it | A single dominant partner stack |
| **3. Local bridge / service** | A small service on the till machine exposing a localhost API | Language-agnostic; matches the Adyen/nexo pattern tills already understand; keeps credentials out of the till process; one artifact to maintain | Something must install and supervise it; a new process on the merchant's machine | Any till with an HTTP client, which is nearly all |
| **4. APIs and docs only** | OpenAPI plus reference examples | Cheapest; maximum flexibility | Every integrator re-solves scanner handling, retries, timeouts and idempotency, and most will get idempotency wrong | Platform-style adopters with strong teams |
| **5. Hybrid** | Combination of the above | Tiers cleanly from no-integration to deep integration | More surface than any single option | See §4 |
| **6. Stop** | Nothing | No maintenance | Abandons the only client that exercises the POS contract; the payment API loses its real consumer and will silently rot | Only if POS is out of product scope |

---

## 4. Recommended product boundary

Adopt the nexo framing: **a clear separation between sale and payment.**

**Open Giftcard POS is responsible for:**

- accepting a payable amount from a till, or from a cashier when there is no
  till integration;
- capturing the card credential, by scan or manual entry;
- the authorize, confirm, cancel, refund conversation with the backend,
  including retries, timeouts, and idempotency;
- device identity and secret custody on the lane;
- returning a clear, machine-readable result: approved amount, remaining amount
  still owed, and a reference the till can print and reconcile against;
- being the reference implementation that proves the backend's POS contract.

**Open Giftcard POS is explicitly NOT responsible for:**

- products, catalogue, pricing, quantities, tax, discounts, promotions;
- inventory, stock, purchasing, suppliers;
- cash drawers, shifts, employee management, tipping, table service;
- receipt printing as a product feature, beyond returning the data a till prints;
- being the merchant's primary till;
- any tender other than Open Giftcard stored value.

The line to hold: **we accept an amount and answer with a result.** Everything
about what was sold belongs to the till.

---

## 5. Recommended architecture

**A local service with a bundled thin cashier UI, plus reference examples.**
That is option 5, weighted as 3 + 1 + 4, and deliberately not 2.

```
┌────────────────────────┐        ┌──────────────────────────────────┐
│ Existing till software │        │  Open Giftcard POS (one binary)  │
│  (products, tax, ...)  │        │                                  │
│                        │ HTTP   │  ┌────────────────────────────┐  │
│  tender: Gift card ────┼───────▶│  │ Local API  127.0.0.1:PORT  │  │
│                        │  JSON  │  └──────────┬─────────────────┘  │
│  ◀─────────────────────┼────────│             │                    │
│  approved 30.00        │ result │  ┌──────────▼─────────────────┐  │
│  remaining 20.00       │        │  │ Payment orchestrator       │  │
│  ref POS-1234          │        │  │ retry, timeout, idempotency│  │
└────────────────────────┘        │  └──────────┬─────────────────┘  │
                                  │             │                    │
┌────────────────────────┐        │  ┌──────────▼─────────────────┐  │
│ Cashier UI (fallback)  │───────▶│  │ Backend client + token     │  │
│ browser, same binary   │        │  │ custody                    │  │
└────────────────────────┘        │  └──────────┬─────────────────┘  │
        ▲                         └─────────────┼────────────────────┘
        │ keyboard wedge                        │ HTTPS, bearer device token
   ┌────┴─────┐                                 ▼
   │ USB scan │                     Open Giftcard backend API
   └──────────┘
```

Components, and why each earns its place:

1. **Headless local service (the core).** Owns the device token, the retry and
   idempotency logic, and all backend conversation. Exposes a small JSON API
   bound to loopback. This is the artifact tills integrate against, and it is
   what the research says tills already expect (§1.2).
2. **Thin cashier UI, served by the same binary.** Delivers your standalone
   workflow for shops with no integration, at near-zero extra cost because it is
   a client of the same local API. One deployable, two tiers of use.
3. **Scanner input as an abstraction, not a driver.** Default adapter is
   keyboard wedge into a focused field, because that needs no driver (§1.4). The
   abstraction exists so a serial or HID POS adapter can replace it without
   touching payment logic.
4. **No SDK initially.** The local JSON API *is* the language-agnostic SDK.
   Publish reference clients as example code in a few languages rather than
   supported packages, so we are not signing up to maintain them.
5. **Configurable adapters** for scanner input, credential presentation, and
   result delivery to the till.

Why not fully standalone only: it cannot close the loop back to the till, so
every sale needs manual reconciliation. Why not SDK-first: unsustainable for one
maintainer and excludes every stack we do not ship.

---

## 6. Real-world workflow

### 6.1 The happy path, with split tender as the normal case

```
1. Till computes total due            50.00
2. Cashier selects tender "Gift card"
3. Till  → POS local API   POST /sale/payment  {amount: 50.00, saleRef: "T-1234",
                                                idempotencyKey: "..."}
4. POS prompts "Scan gift card"
5. Cashier scans QR  →  credential captured (keyboard wedge, Enter-terminated)
6. POS  → backend  POST /v1/pos/payment-provisions
                   {paymentToken, amount: 50.00, posTransactionReference: "T-1234"}
        ← backend  partial approval: held 30.00 of 50.00 requested   ← NEEDS §2.2 GAP 1
7. POS shows cashier: "Card covers 30.00. 20.00 still due. Confirm?"
8. Cashier confirms
9. POS  → backend  POST /v1/pos/payment-provisions/{id}/confirm  {amount: 30.00}
        ← backend  confirmed, ledger transaction id
10. POS → till     200 {approved: 30.00, remaining: 20.00,
                        reference: "...", ledgerRef: "..."}
11. Till takes 20.00 by another tender and prints both lines
```

Step 6 is the change that makes this real. Without it, step 6 returns a refusal
and the cashier is stuck.

### 6.2 Cancellation

Cashier or customer aborts before confirm: POS calls `.../cancel`, the hold is
released, nothing is posted, and the till is told `declined, reason: cancelled`.
After confirm, cancellation is not a cancel; it is a refund (§6.5).

### 6.3 Timeout

Two distinct cases, and conflating them is how money goes missing.

- **Timeout on create.** The credential may have been consumed. The POS must be
  able to ask "did my request succeed?" using its own idempotency key, and
  either recover the provision or learn there is none. **This needs the
  idempotency key from §2.3.** Today the only safety net is the 2-minute expiry.
- **Timeout on confirm.** Safe today: confirm is addressed by provision id and
  retrying returns the original outcome. The POS should retry with backoff until
  the hold window closes, then report indeterminate and tell the cashier to
  check before re-charging.

The till-facing contract should distinguish `declined` from `indeterminate`. A
till that treats indeterminate as declined will double-charge customers.

### 6.4 Retry

All retries carry the original idempotency key. The POS never invents a second
payment for one sale attempt. On repeated network failure the result is
`indeterminate` with the reference, never a silent second attempt.

### 6.5 Refund

Till sends a refund for a sale reference; POS calls `.../refunds` with an
idempotency key. Partial and repeated refunds are already supported and bounded
by `RemainingRefundableAmount`. This works today.

### 6.6 Partial payment

Covered in 6.1. Note the platform-side subtlety: because holds are visible via
`IPaymentReservationQuery`, a partial approval must reserve only the approved
amount, leaving the rest of the card spendable if the customer abandons the sale.

---

## 7. Security considerations

- **Local API binding.** Bind to loopback only. Anything on the LAN is a lane
  impersonating another lane. If cross-machine is ever needed, it needs mutual
  TLS, which is what Adyen requires for its local integration (§1.2).
- **Local API authentication.** Loopback alone is not authentication; any local
  process can call it. A per-install shared secret the till presents is the
  minimum, otherwise malware on the till can drain presented cards.
- **Keyboard wedge leakage.** The most likely real-world credential leak is a
  scan landing in the till's search box or log because focus was wrong. Document
  it, and prefer a POS window that can assert focus before prompting.
- **Device secret custody.** The POS client secret is currently in .NET
  user-secrets and is unrecoverable by design. That is right for a workstation,
  but it means lane re-provisioning must be a supported, documented operation,
  not an incident.
- **Data Protection keys.** Resolved 2026-08-24. Development uses an ignored
  local key ring; every other environment must name durable shared storage or
  the till refuses to start. A restart-level test submits an antiforgery token
  issued by the previous host instance.
- **Replay.** Already handled well: credentials are single use with a 60-second
  TTL, and unknown, expired, consumed and replayed are refused identically.
- **Balance-inquiry oracle.** If we add inquiry (§2.2), it must require card
  presentation, be rate-limited per terminal, and be audited. Otherwise it is a
  balance-harvesting endpoint.
- **Operational risk.** A lane holding value it never confirms or cancels is the
  worst failure: the customer's money is invisible for the window. The expiry
  worker is the mitigation and must be verified in a real deployment, not just
  in tests.

---

## 8. OSS extensibility, without overengineering

Four seams, no plugin framework:

1. **Configuration over code** for branding, currency display, terminal
   identity, backend URL, timeouts, and the local API port.
2. **Adapter interfaces at exactly three points**: scanner input, credential
   presentation, and till result delivery. Anything more is speculation.
3. **The local JSON API is the extension point.** Any language can integrate
   without touching our code, which is what makes forking rare.
4. **Reference examples, not supported SDKs.** Small, deliberately unpolished
   client samples that say "copy this", so we owe no compatibility promise.

The test of success: a company should be able to change branding and till
integration **without forking**, and fork only to change payment behaviour
itself.

---

## 9. Build or not build

**Build, but build the local service, not the standalone app, and fix the
backend first.**

Reasons to build: the backend's POS contract has no other consumer, and an
unexercised contract rots. Every trap in the handoff came from code that had
never run. A reference client is also the only honest way to claim the payment
API is integrable.

Reasons the current POS repository should not simply be continued: it is a
demonstration till with a mock cart, which is the wrong product. Continuing it
means maintaining a fake supermarket.

**Sequence I recommend:**

1. Close the payments gaps as backend 1.0 work: partial approval, POS balance
   inquiry with presentation, idempotency key on provision create.
2. ~~Close the four POS repository gaps (contract pin, `/health/ready`, security
   headers, Data Protection).~~ Completed 2026-08-24.
3. Rebuild the POS as the local service plus thin cashier UI. Delete the mock
   cart.
4. Publish reference integration examples.

If you would rather not do 1, then the honest move is **6, stop**: without
partial approval a gift card POS is not usable in a supermarket, and shipping
one would be the same kind of claim the README currently makes about releases.

---

## 10. Questions that need your decision

1. **Partial approval.** Should a hold for more than the available balance
   approve the lesser amount, or keep refusing? This is the single decision that
   determines whether the POS is usable in a supermarket.
2. **Balance inquiry.** Add a POS-facing inquiry that requires card presentation
   and is rate-limited and audited, or keep the balance invisible to tills and
   rely on partial approval alone?
3. **Activation and top-up at the till.** In scope, or explicitly out? Industry
   expects it; it is a significant surface.
4. **Primary integration artifact.** Confirm the local service plus thin cashier
   UI, rather than a standalone app or an SDK.
5. **The existing POS repository.** Rebuild in place, or start a new repository
   and archive it? It is already public and does not meet the other repos'
   standards.
6. **Offline.** Confirm we state "requires connectivity" as a supported
   limitation rather than building store-and-forward.
7. **Target deployment.** Windows-only lanes, or Linux too? This decides
   packaging and how the service is supervised.
