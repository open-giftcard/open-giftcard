# Accepted API contract baseline

`backend.openapi.json` is the served OpenAPI document at the accepted backend
commit. Unlike the copies in the client repositories, which exist so a client
can detect the backend moving away from it, this copy is the **baseline for
compatibility**: `scripts/Test-ApiCompatibility.ps1` diffs the document this
build serves against this file and fails on a change that `VERSIONING.md`
forbids within a major version.

- Repository: https://github.com/open-giftcard/open-giftcard
- Commit: `a8a506ae7de8871ce24462e1e838eded3fba67bc`
- Endpoint: `/swagger/v1/swagger.json`
- SHA-256:
  `DE48FE7764D9C58DD4CEA3E9DC6A3B4698A29527CD0CBE386E9C4360AB32B663`

The three client repositories hold a byte-identical copy of this file, and
`RELEASE_COMPATIBILITY.json` records the same commit and hash in all four.

## When to move the baseline

Additive change does not require moving it. New endpoints, new optional request
fields, and new response fields all pass the compatibility check against an
older baseline, and leaving the baseline where it is keeps the guarantee
anchored further back.

Move it when a major version is released, and record the new commit and hash
here and in `RELEASE_COMPATIBILITY.json` in the same change. Moving it to
silence a failure is the one thing it must never be used for: the failure is
the check working.
