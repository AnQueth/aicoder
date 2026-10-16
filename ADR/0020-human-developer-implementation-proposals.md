# 0020: Collect human implementation proposals before combined planning

**Status:** Accepted

## Context

Plan approval alone does not capture how each human developer wants to implement
a feature. The user wants AI to combine all developer inputs, either before an
initial plan or after an initial AI draft. Everyone may explicitly contribute no
additional input. A requested revision must allow updated ideas and require
everyone to reconfirm.

## Decision

Add opt-in `planningMode` values `proposals-first` and `draft-first` to feature
intake. The Vue client offers both and defaults to proposals first. Omission
preserves existing API/console behavior; existing runs are not migrated.

Keep the original orchestration name and its activity sequence for existing
Durable Task histories. Opted-in runs start a separate orchestration entry point
that shares the existing workflow logic but adds the contribution gates.
Draft-first creates a non-approvable initial plan revision, then collects input
for revision 2. This draft counts toward the existing five-plan-revision limit.

Select and persist the human reviewer group before collecting proposals. Store
settings and revisioned input rounds in the existing workflow-artifact table;
no new database schema or migration is necessary. Each participant submits either
an implementation proposal or an explicit no-additional-input response for the
exact target revision. Carry earlier text into new rounds as an unconfirmed
draft. Freeze a round when everyone confirms, using the existing artifact row
version to prevent lost concurrent updates. A conflicting request returns 409
and must be refreshed and retried.

The final contribution persists an outbox message in the same transaction.
Delivery wakes the durable workflow with a revision-bound event, and the workflow
checks persisted readiness before generating the plan. Duplicate or stale
events do not bypass the gate. This avoids repeatedly polling the database from
the orchestration while waiting on people.

Supply all frozen inputs, including participant identity/roles and explicit
no-input responses, as structured planner evidence. Tell the planner to combine
feasible suggestions within approved scope and expose conflicts as open
questions rather than silently discarding inputs. Preserve independent
developer plan review and final approval before code generation.

## Consequences

- Humans can shape the implementation, not only approve an AI-authored plan.
- Proposal rounds remain inspectable alongside the exact resulting plan revision.
- A single missing confirmation blocks combined planning; no timeout invents
  a contribution or implicitly approves anything.
- Shared development-key callers still act on behalf of named participants.
  This does not introduce individual login or role-based authorization.
- Console-created legacy runs keep their current path. The automated API client
  also supports explicitly selecting either planning mode and submitting
  revision-specific proposals or no-input responses. Interactive collection
  prompts each developer. Scripted mode stops if any selected developer lacks
  an explicit input for the target revision; it never invents human ideas.
- New workflows require refreshing both API and workflow resources. Databases
  and existing durable state must be preserved.
