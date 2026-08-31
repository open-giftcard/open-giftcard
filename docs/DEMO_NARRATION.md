# Demo narration

A script for talking about the platform *while* you drive it. Each act says
what to click, what to say about it, and what to say if someone asks the
obvious follow-up.

Companion to `DEMO_RUNBOOK.md`, which covers setup and the order of the four
applications. This document is about the sentences.

**One rule that makes the whole thing land:** show the screen first, then say
what is behind it. "Here is the card register" is a feature. "Here is the card
register, and notice it will not tell you what the employee has left to spend"
is a design decision, and that is the part worth the room's attention.

---

## Before you start

| Application | Port | Who it is for |
| --- | --- | --- |
| Backend API | 5143 | Nobody. It is the authority the other three ask. |
| Portal | 5179 | platform staff and company staff |
| Cardholder | 5180 | The employee, on a phone |
| POS | 5190 | The till |

Say early, once: **four applications, one backend, and the backend is the only
thing that decides anything.** Every screen you are about to show is asking it
questions. That sentence saves you explaining it four times.

---

## Act 1 — Signing in to the portal

**Do:** open the portal on 5179 and sign in as the platform operator.

**Say:** signing in returns two tokens. A **JWT access token good for fifteen
minutes**, and a **refresh token good for thirty days**.

**Then say the part people do not expect:** neither of them reaches the
browser. The portal is a same-origin BFF, so the tokens stay on the server and
the browser gets an `HttpOnly` cookie that points at them. A cross-site script
cannot read an `HttpOnly` cookie, and there is no token in `localStorage` to
steal.

**If asked "why fifteen minutes":** because a leaked access token is only
useful for as long as it lives, and the refresh token is the thing under lock
and key. Refreshing rotates it: the old one is consumed and a replacement
issued in the same family. If a consumed token is ever presented again, that is
treated as a compromise and the whole session family is revoked, because the
only way to present a used token is if someone copied it.

**Worth a beat:** the JWT says who you are. It does not say what you may do.
Permissions are read from PostgreSQL on every request, so revoking access takes
effect at once instead of when a token happens to expire.

---

## Act 2 — Creating a customer and funding it

**Do:** platform workspace. Create a customer organization, then allocate
corporate credit.

**Say:** this is the platform operator selling gift-card value to a company. Watch what the
allocation *is*: not a number in a field being edited, but a **posted,
balanced, immutable ledger transaction**.

**The line worth landing:** there is no balance column anywhere in this system
that anyone can set. A balance is derived by adding up ledger entries. That
means value cannot be created by a bug in a screen; it can only be created by a
posting, and every posting balances.

**If asked "what if you get it wrong":** you do not edit it. You post a
compensating transaction. The original stays, because financial history that
can be rewritten is not history.

**If asked about double submission:** every financial operation carries an
idempotency key, unique in the database. Sending the same allocation twice
returns the first result. It does not allocate twice.

---

## Act 3 — Distributing to employees

**Do:** sign in as the Company Administrator. Go to Cards, then the bulk batch,
and **upload the spreadsheet**.

**Say while it parses:** that file never left the browser. It is read here,
turned into the same form the manual path uses, and reviewed before anything is
issued. There is no upload endpoint, so there is no stored file to protect and
nothing to scan.

**Point at the names:** they are shown so you can check you matched the right
people to the right addresses, and then they are dropped. The platform stores
no employee names. It knows an email address or a phone number, because that is
what it needs to deliver a card, and nothing more.

**Do:** submit, and show the results.

**Say:** each row became a gift card funded from the company's corporate
credit, and each recipient got an activation message. The recipient **does not
need an account first** — that is the whole point. The invitation carries a
single-use token that expires, and only its hash is stored.

**If asked "what if one row is wrong":** this batch is all-or-nothing, so
nothing is issued and you fix the file. For thousands of rows there is a
durable asynchronous path in the backend where each row settles independently;
the portal side of that is planned and written down.

---

## Act 4 — The employee, on a phone

**Do:** open the activation link on a real phone. Set a password. Land on the
card.

**Say:** this application ships **no JavaScript at all**. It is server-rendered
HTML. On a cheap phone on a bad connection in a shop, that is not a purist
position, it is the difference between working and not.

**Show the flip, the theme switch, the language switch.** Then say: all of that
is CSS and form posts. The countdown you are about to see on the payment screen
is a CSS animation, not a timer.

**If asked why that matters beyond speed:** the page that shows a payment
credential runs under `script-src 'none'`. There is no script on it, so there
is no script that can read the code off it.

---

## Act 5 — Paying at the till

**Do:** on the phone, generate a checkout code. On the POS at 5190, scan or
type it.

**Say while the countdown runs:** that code is 256 bits of randomness and
**means nothing by itself**. It is not the card number. It does not encode who
you are, which card it is, or how much is on it. The backend looks it up, and
that is the only thing that can turn it into a payment.

**Say:** it lives sixty seconds and is single-use. Two tills scanning the same
code cannot both succeed — they serialize, and the second one is refused.

**The part that usually gets a question — "so is the money gone when I scan
it?":** no. Scanning creates a *hold* for two minutes. The hold reserves the
value so it cannot be spent twice, but nothing is posted to the ledger yet.
Confirming posts it. Cancelling or letting it lapse releases it with no
financial record at all, because nothing happened.

**Show the phone updating** to the confirmed amount. Say: that panel is polling
an owner-only status endpoint. It never sends the payment code back.

---

## Act 6 — What the company can and cannot see

**Do:** back in the portal, open the **Register**.

**Say:** here is every card the company funded, including the ones already with
employees. Inventory alone would have lost sight of a card the moment it became
useful to somebody.

**Now point at the Remaining column, at a claimed card, and say the sentence
this whole act exists for:** it is blank. Deliberately. The company funded the
card and can see what it put in. What the employee has left is a record of what
that person has been buying, and it is theirs. If you want it, you ask them, or
you ask the platform operator, who can see it for fraud handling.

**If asked "isn't that our money":** until it is claimed, yes, and you can see
the balance then. After it is claimed it is a benefit that has been given. The
aggregate is still on the finance summary, because the company legitimately
needs the outstanding total; what is withheld is per-person spending.

**Also show:** the recipient contact is masked here too.

---

## Act 7 — Isolation, if you have a technical audience

**Do:** stay in the portal as the company administrator.

**Say:** this company cannot see another company's anything. That boundary is
not in the screen and it is not in the API. It is in **PostgreSQL, as
row-level security.**

**Why say it that way:** application code has to remember to filter. Every
query, forever, including the one written on a Friday. RLS does not have to
remember. If the filter is forgotten, the database returns nothing rather than
somebody else's data. The runtime database role cannot bypass it — it is not a
superuser and does not hold `BYPASSRLS`.

**One more, if they are enjoying it:** the audit log is append-only at the
privilege level. The application's database user has INSERT and SELECT on it
and no UPDATE or DELETE. It is not that the code does not delete audit records.
It is that it cannot.

---

## Questions you should expect

**"Is this ready to deploy?"**
No, and the gaps are known rather than discovered. The audit signing key needs
a managed KMS or HSM; it uses local development keys today and deliberately
refuses to run that way outside development. There is no container packaging,
no TLS or ingress, no backups or restore drill, and it has never been deployed
to staging. Those are the four things between here and live, and they are
operations work, not features.

**"What happens if the backend goes down mid-payment?"**
The hold expires after two minutes and the value returns. Nothing is posted
unless a confirmation completes, and confirmation is one transaction.

**"Can someone forward a share link and steal the value?"**
They can forward it, and it will not help them. A generic link needs a
six-digit PIN, expires in twenty-four hours, is single use, and locks
permanently after five wrong attempts. It also cannot create an account: it can
only be claimed by someone already signed in as a different person. Creating an
account requires the contact-bound invitation, because possession of a link is
not proof of identity.

**"How do you know the balances are right?"**
There is a read-only reconciliation that recomputes the domain records against
the ledger and reports differences. It reports them. It never repairs them,
because a report that silently fixes what it finds cannot be trusted to tell
you the truth about what it found.

---

## If something breaks

Stay on one host for the whole journey. Moving between `localhost` and a
hotspot address mid-flow breaks the antiforgery cookie.

If a screen misbehaves, the honest line is the best one: this is a system in
development, here is what it is doing, here is why. The architecture is the
demo. A UI glitch does not undermine a ledger that balances.
