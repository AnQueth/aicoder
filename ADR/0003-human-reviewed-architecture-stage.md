# 0003: Require human-reviewed architecture before planning

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

Feature requests need a repository-grounded technical assessment before developers
commit to an implementation plan. Moving directly from business requirements to a
plan can hide affected components, trade-offs, risks, and unresolved design
choices. The analysis must also be tied to the exact repository snapshot and skill
revision that produced it.

## Decision

After business-analyst approval, generate a revisioned architecture analysis from
the approved specification and the run-pinned repository snapshot. Every reported
component impact must cite a file in that inspected snapshot. Persist the source
commit, input artifact revisions, and exact architecture skill revision with the
analysis.

Require a human approve, revise, or reject decision bound to the current analysis
revision before developer planning can begin. Revision requests create a new
analysis using the prior result and feedback; rejection ends the run. Enforce the
approval requirement in workflow sequencing, artifact persistence, and code
generation.

## Consequences

- Planning and code generation cannot proceed from an unreviewed or rejected
  architecture analysis.
- Reviewers can trace findings to the source snapshot and skill revision that
  produced them.
- Architecture review adds a human wait and may lengthen run time.
- API actor labels remain audit metadata, not verified identities; real role
  authorization is still required before production use.
- Architecture decision proposals can suggest when an ADR may be useful, but a
  human still decides whether to create or accept that record.
