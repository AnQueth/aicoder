# 0015: Use a QA specialist in developer planning analysis

- **Status:** Superseded by [0016](0016-user-defined-per-run-specialists.md)
- **Date:** 2026-10-15

## Context

Planning needs explicit unit-test deliverables and a QA test plan. The user chose
a specialist in the existing developer analysis stage rather than a mandatory
new workflow stage.

## Decision

Initialize `developer-qa/general/test-planning` through the console and local
verification script when absent. Preserve existing prompt revisions and explicit
disabled state. Direct API callers configure it through the shared skill API.

Enabled QA skills use the existing submission-time snapshot, sequential specialist
activities, complete-set verification, synthesis, and planning context. The QA
analysis calls for unit tests and a QA test-plan document with acceptance-criteria
mapping, scenario IDs, setup/data, actions, expected results, automation/manual
classification, negative/regression coverage, and evidence-backed commands.
The planner carries these deliverables into implementation steps and records
unresolved coverage/tooling gaps as open questions.

## Consequences

- No new workflow stage, schema, review gate, or execution permission is added.
- QA can be disabled through the shared registry; it is not mandatory for direct
  API submissions. Existing runs keep their captured skills.
- An enabled QA skill adds one specialist activity and increases planning time.
- These are model-generated planning artifacts, not proof that tests were
  implemented, executed, passed, or signed off by QA or a UAT owner.
