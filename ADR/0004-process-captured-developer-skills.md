# 0004: Process every captured developer skill

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

Different features need different developer perspectives, such as UI, backend,
security, or testing. A fixed list of roles would make the system hard to extend,
while selecting only skills an agent considers relevant could silently skip a
human-authored skill. A skill can also be edited while a run is waiting for human
review, so the exact skills and revisions used by that run must be stable.

## Decision

Represent each developer role as a normal revisioned skill whose `roleId` starts
with `developer-`. At feature submission, snapshot every enabled skill in that
namespace, including its complete prompt and revision reference. Reject a new
feature request if there is no enabled developer skill.

After architecture approval, run one bounded Durable activity for each captured
skill in stable role/domain/skill order. Persist each result independently with
that skill's exact revision and the pinned repository commit. Before planning,
aggregate the results and verify that every captured skill has exactly one
matching result. The aggregate records the full skill-revision set and is passed
to cross-role synthesis and plan generation. New skills and skill revisions
apply to later feature runs; they do not change the snapshot for a run already
submitted. This new product does not preserve older orchestration/activity
implementations to replay prior histories.

## Consequences

- Humans can add an arbitrary number of developer roles through the existing
  generic skill registry without code changes.
- Every enabled developer role participates; no model decides which configured
  role to skip.
- Specialist generation time and model usage grow with the number of enabled
  skills.
- Each activity stays within its own execution time limit; workflow progress can
  resume from already-persisted results after an activity retry.
- Workflow changes may invalidate histories from older implementations. Before
  deploying a changed workflow, ensure no active instance depends on its prior
  implementation; no legacy replay path is maintained.
- Skill prompts are persisted with run snapshots and handled as untrusted context.
- The planner receives all specialist findings and remains subject to the existing
  human plan-review gate.
