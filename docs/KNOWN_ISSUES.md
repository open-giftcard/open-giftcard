# Known Issues

Defects and rough edges found by using the system, as opposed to gaps that were
planned. Deliberate omissions — managed key custody, SMS delivery, deployment,
staging certification — live in `README.md` under *What is not done* and are not
repeated here.

Each entry names the repository that owns the fix, because the platform spans
four of them.

**Found:** 2026-08-07, during end-to-end demonstration setup.

**Fixed since:**

* Direct share invitations were not being sent at all. That genuine bug is
  corrected; see the sharing fix on `fix/share-invitation-email`.
* On 2026-08-10, rejected antiforgery submissions stopped returning a bodyless
  downloadable 400. The cardholder app now sends only the framework's
  antiforgery failure result to a readable session-expired recovery page;
  application-authored 400 responses remain unchanged.
* On 2026-08-10, the two sharing options stopped describing their
  precondition and now lead with what happens to the recipient: "They will
  need to sign in" against "They can set up an account from the message". The
  security boundary is unchanged; only the copy moved.
* On 2026-08-10, the checkout page gained an exact-owner confirmation panel.
  It uses a sandboxed same-origin frame with meta refresh, keeps
  `script-src 'none'`, and shows pending, active, paid amount, cancelled, or
  expired state without persisting or sending the QR/numeric credential back.
* On 2026-08-11, a newly activated recipient is told exactly which email or
  phone their password belongs to, once, on the authenticated card list. The
  exact contact comes from their own `/me` record; lookup failure falls back to
  the masked claim result instead of failing the page. Cardholder verification:
  165 tests and a warning-free Release build.
* On 2026-08-11, the portal stopped collapsing every distribution conflict into
  one generic message. It now maps the backend's not-yet-valid, expired,
  ineligible, idempotency-reuse, and concurrent-conflict codes to distinct safe
  guidance while retaining the generic fallback for unknown codes. Portal BFF
  verification: 99 tests.
* On 2026-08-11, an expired checkout presentation stopped leaving a readable QR
  on screen with renewal separated from it. The QR now blurs at its real
  sixty-second expiry and reveals a centered manual renewal action on the same
  page. Renewal stays an antiforgery-protected POST with no JavaScript or
  automatic issuance. Cardholder verification: 166 Release tests.

---

## Open issues

None currently recorded.

## Pattern worth noting

The fixed antiforgery defect, sharing-choice copy, activation reminder, portal
conflict mapping, and expired checkout presentation shared one failure: **the
system knows exactly what is wrong and the interface does not pass it on.** The
fixes now preserve that useful information without weakening the existing
authorization or data-minimisation boundaries.

Keep that failure pattern in mind when future interfaces translate backend
state into user-facing guidance.
