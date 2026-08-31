# Demo Runbook

For showing the platform to someone who has not seen it. Runs on the four real
applications, not on `/demo`.

This document covers setup and the order to visit things in.
[`DEMO_NARRATION.md`](DEMO_NARRATION.md) covers what to *say* at each screen,
and the questions to expect.

**Why not `/demo`.** It is mapped only inside `if (app.Environment.IsDevelopment())`
and 404s everywhere else, so it can never be deployed. It is 3,841 lines of one
page whose headings describe architecture rather than the business: *"Move value
through the ledger"*, *"Shape access without widening tenancy"*. It existed to
prove backend behaviour before any client did. All three clients now exist, and
almost every operation it demonstrates has a real screen, so showing it means
showing a developer tool instead of the product.

---

## Before anyone is in the room

Nobody should watch setup. Do this ahead of time.

### Services

| Application | Port | Start from |
| --- | --- | --- |
| Backend API | 5143 | `open-giftcard` |
| Portal (BFF) | 5179 | `open-giftcard-portal` |
| Cardholder | 5180 | `open-giftcard-cardholder` |
| POS | 5190 | `open-giftcard-pos` |

Confirm all four before starting:

```bash
for p in 5143 5179 5180 5190; do printf "%s " $p; curl -s -o /dev/null -w "%{http_code}\n" http://localhost:$p/health; done
```

### The one step with no screen

Creating the very first platform administrator. It is a secret-protected
one-time endpoint by design (ADR-029) and deliberately has no UI, because an
endpoint that mints platform authority should not sit behind a button.

Everything after this point happens in a real application.

### Demoing to a phone

The cardholder app is the one worth showing on a real phone. Connect the phone
to the machine's hotspot and use the machine's hotspot address rather than
`localhost`.

**Pick one host and stay on it for the whole journey.** Moving between
`localhost` and the hotspot address mid-flow breaks the antiforgery cookie, and
the failure currently presents as the browser offering to download a file named
`confirm` (`KNOWN_ISSUES.md` §1). It looks like a crash and is not.

---

## The story, in four acts

Each act is one application. Say what is happening in business terms; the
architecture belongs in the second half.

### Act 1 — the platform operator onboards a corporate customer (portal, 5179)

Sign in as the platform operator.

1. **Create the customer organization.** Platform workspace.
2. **Allocate corporate credit.** Platform funding workspace. This is the platform operator
   granting the company spendable value.
3. **Assign the first Company Administrator.**

*What to say:* the platform operator sells gift-card value to a company. Nothing here is a number
in a field being edited. The allocation is a posted, balanced, immutable ledger
transaction, and it is the only way value can enter the system.

*Worth pausing on:* the platform operator can do this and cannot see another
customer's data. That boundary is in the database, not in the screen.

### Act 2 — The company distributes to an employee (portal, 5179)

Sign in as the Company Administrator.

1. **Issue a gift card** from corporate credit. Gift cards workspace.
2. **Distribute it** to an email address. Distribution.
3. Optionally show **bulk distribution** for the payroll-sized case.

*What to say:* the company divides its corporate credit into cards for named
people. The recipient does not need an account yet, and receiving a card never
makes them a member of the company's organization in the system.

### Act 3 — The employee activates and holds the card (cardholder, 5180)

Open the activation link, ideally on the phone.

1. **Activate** and set a password.
2. **My cards.** Tap the card to turn it over.
3. **Generate payment code.** QR plus a 12-digit code, with the countdown.

*What to say:* this is the employee's own wallet. The company can no longer take
this card back, and cannot see what the employee spends. The code on screen is
meaningless if photographed: it carries no card number, no balance, no identity,
lasts 60 seconds, and works exactly once.

*Known rough edge:* the cardholder screen does not yet confirm the payment
afterwards (`KNOWN_ISSUES.md` §5). Say so before it becomes a question.

### Act 4 — Paying at the till (POS, 5190)

1. Mock basket with a total.
2. **Enter the 12-digit code** from the phone, or scan.
3. **Confirm** the sale.

*What to say:* the till authenticates as a registered device, separately from
the customer. Possessing the code is not permission to charge, and being a
registered till is not permission to charge a specific card. Both are required.

*Then return to the portal:* the payment appears in platform payment reporting,
and the company's finance view shows value spent. Same event, two audiences,
neither of them a copy of the other.

---

## Second half: how it works, and why

Once the journey has landed, the questions become architectural. Two documents
already exist for this and are better than talking from memory:

* **How to use the system**, per audience —
  <https://claude.ai/code/artifact/124c04bb-5a3f-4bd9-ba01-d6d6dde6bfdc>
* **Decisions and architecture**, with diagrams —
  <https://claude.ai/code/artifact/384e3cd1-aad8-46eb-b42a-7f309dc10d1d>

`docs/DECISIONS.md` is the full record: 52 accepted decisions, each with the
options considered and why the others were rejected. Worth showing the file
itself briefly, because the fact that rejected options are written down is the
point.

Four decisions carry most of the story and are worth having ready:

| Question a supervisor asks | Where it is answered |
| --- | --- |
| "How do you know the money is right?" | ADR-014. Posted, balanced, immutable double entry. Balances are derived, never stored as an editable number. Corrections are compensating entries, so history is never rewritten. |
| "What stops one customer seeing another?" | ADR-005, ADR-020. PostgreSQL Row-Level Security is the barrier, not application code. The runtime database role cannot bypass it. A connection with no verified context sees nothing. |
| "What if the till charges twice?" | ADR-018. The purchase identity is the server-issued credential, not something the till chooses. A retry returns the original outcome; a buggy till cannot double-charge and cannot block a legitimate sale. |
| "Why is the QR safe on a screen?" | ADR-017. 256 random bits that encode nothing. No card, no owner, no balance. Sixty seconds, single use, and resolved server-side. |

---

## Be honest about what is not done

Say this before being asked. It reads as competence, not weakness. The same
list is in `README.md` under *What is not done*, and the gaps that carry
security weight are in `SECURITY.md`.

1. **Managed audit custody.** Signed tamper-evident checkpoints exist; the
   KMS/HSM and immutable-storage adapters are deployment selections not yet
   made.
2. **Deployment packaging, operational readiness, staging certification.**
3. Five known usability defects, each with a proposed fix, in
   `docs/KNOWN_ISSUES.md`.

The published tags are source-and-verification baselines, not a deployed system,
and `v0.4.0-rc.2` is the first candidate whose recorded evidence matches what is
actually merged.

---

## If something goes wrong mid-demo

| Symptom | Cause | Say and do |
| --- | --- | --- |
| Browser offers to download a file called `confirm` or `signout` | Antiforgery cookie, from switching between `localhost` and the hotspot address | Known issue §1. Clear that site's cookies, stay on one host. |
| A distribution is refused as a conflict | The card's validity window has not opened yet | Known issue §2. The portal collapses the specific reason into a generic message. |
| The cardholder shows nothing after the cashier confirms | No confirmation is implemented yet | Known issue §5. Show the updated balance instead. |
| An old build appears to be running | Something started from `bin\Debug` | Everything is built Release. Restart with `-c Release`. |
