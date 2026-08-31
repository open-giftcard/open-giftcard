# Documentation

The README is the entry point and the quickest route to a running system. These
documents are what it hands off to.

The code is the authority over all of it. Where a document and the
implementation disagree, the architecture tests, the migrations, and the
integration suite describe the system precisely and the document is wrong.

## Start here

| Document | Read it for |
| --- | --- |
| [`PROJECT_DEFINITION.md`](PROJECT_DEFINITION.md) | What the platform is for, its scope, and the phased roadmap it was built against. |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | How the modular monolith is put together: modules, boundaries, dependency direction, transaction and audit design. |
| [`DOMAIN_RULES.md`](DOMAIN_RULES.md) | The vocabulary and the invariants. Read this before changing anything that touches value. |
| [`DECISIONS.md`](DECISIONS.md) | The architecture decision records. Source comments cite these by number, for example `ADR-019`. |
| [`CODEMAP.md`](CODEMAP.md) | Where things live, and where to start looking for a given behaviour. |

## Working on it

| Document | Read it for |
| --- | --- |
| [`FRONTEND_INTEGRATION.md`](FRONTEND_INTEGRATION.md) | Building a client against the API: authentication, the organization header, error shapes, and the contract pin. |
| [`DEPLOYMENT.md`](DEPLOYMENT.md) | Native archives, migration, staging evidence, recovery, and rollback. |
| [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) | Defects found by using the system. Planned omissions are in `README.md` and `SECURITY.md` instead. |
| [`POS-STRATEGY.md`](POS-STRATEGY.md) | Why the point-of-sale client is shaped the way it is, and what it deliberately is not. |

## Showing it to someone

| Document | Read it for |
| --- | --- |
| [`DEMO_RUNBOOK.md`](DEMO_RUNBOOK.md) | Setting up a demonstration across the four applications, and the order to visit them in. |
| [`DEMO_NARRATION.md`](DEMO_NARRATION.md) | What to say at each screen, and the questions to expect. |

## What is not here

Working notes, task history, handoff documents, and internal review records stay
out of the repository. They describe how the work was sequenced rather than how
the system behaves, and they date badly. `CHANGELOG.md` and the commit history
cover what actually changed.

Release policy lives at the repository root rather than here:
[`VERSIONING.md`](../VERSIONING.md) for what each version number promises, and
[`RELEASE_READINESS.md`](../RELEASE_READINESS.md) for the gate on the current
candidate.
