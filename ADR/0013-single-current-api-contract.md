# 0013: Maintain one current API contract

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

BA skill configuration still had a separate alias API even though all roles
share the revisioned agent-skill registry. The console and verification script
used those aliases. Submission responses also exposed two names for the same run
identifier, and the console recognized an approval status the workflow no longer
persists.

## Decision

Maintain one current interface without compatibility aliases. All role skills
use the agent-skill listing and revision endpoints. BA skills are identified by
role `business-analyst`, domain `general`, and skill ID; the analyst identity is
an actor label and is not part of the skill key.

Remove the BA alias routes, their DTO/validator, and their client wrappers.
Update the console and smoke script together. Feature submission returns `runId`,
`status`, and `statusUrl`; it does not return a second identifier alias.
The console recognizes current persisted outcomes only. The `plan_approved`
audit event remains valid, but is not a terminal run status.

Remove smoke-script handling for obsolete generated skill names. Operational
documentation describes current behavior, not retired modes or interfaces.
Historical ADRs remain decision history rather than supported interface guides.

## Consequences

- All callers must use the current contract; no redirects or fallback routes
  are provided.
- Deploy the API, worker, console, and scripts together.
- This does not remove current database migrations, explicit offline test
  providers, or settings required by current infrastructure.
- Finish active workflow instances before changing their orchestration.
