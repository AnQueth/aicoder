# 0002: Immutable skill revisions and stage artifact provenance

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

Collaborator is expanding from one business-analyst prompt to human-maintained
skills for multiple AI roles and domains. Humans may update a skill while a run
is active. A run must remain explainable: reviewers need to know which skill
versions and upstream artifacts produced each result, and a skill change must
not silently alter work already in progress.

## Decision

Store a stable skill identity separately from immutable, numbered prompt
revisions. The skill identity points to its active revision; a revision records
its author, change reason, and creation time.

Persist stage outputs as revisioned artifacts with their input artifact
references, exact skill revisions, and pinned repository commit. Bind questions
and human reviews to a specific artifact revision.

New runs use the active skill revision. Existing work continues with its
captured revisions unless a human explicitly requests re-evaluation. Re-evaluation
creates a new artifact revision and marks affected downstream artifacts or
decisions stale; it does not rewrite historical outputs.

## Consequences

- Skill history and artifact provenance remain auditable.
- Humans can improve skills at any time without changing active model work
  invisibly.
- Explicit re-evaluation, stale-state propagation, and approval checks are
  required before regenerated work can proceed.
- Existing BA prompt snapshots remain supported while the general skill
  registry is introduced incrementally.
