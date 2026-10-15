# 0016: User-defined prompts and per-run specialist selection

- **Status:** Accepted
- **Date:** 2026-10-15
- **Supersedes:** [0015](0015-qa-specialist-during-planning.md)

## Context

Users must own specialist prompts, and different features need different
specialists. Automatically creating QA/general specialist prompts and embedding
QA policy in agent definitions makes that configuration less flexible.

## Decision

Feature submissions accept optional `developerSkills` keys containing `roleId`,
`domain`, and `skillId`. Omitted/null selection captures enabled registry skills;
an explicit list captures only those skills; an empty list captures none.
Invalid or duplicate keys fail validation. Explicit missing/disabled skills fail
rather than silently reverting to registry defaults.

Capture active prompt revisions at submission. Persist the canonical requested
selection separately from captured revisions for idempotency: selection order is
irrelevant, while omitted, explicit, and empty selections are distinct requests.
Registry edits do not change an existing run or an idempotent replay.

Remove automatic QA/general specialist setup and role-specific QA instructions.
User prompts specify expertise and deliverables, including any unit-test or QA
plan requirements. Agent definitions retain generic execution boundaries and
output contracts. The smoke script explicitly selects its fixture specialist.

When none are selected, store an empty specialist bundle, skip model synthesis,
and plan using approved requirements/architecture. Keep all human approval gates.
The CLI exposes repeatable `--specialist role/domain/skill` and `--no-specialists`.

## Consequences

- Runs may use different subsets without changing global enabled state.
- No QA/general specialist prompt is installed or rewritten by the console.
  Existing configured skills remain user-managed registry entries.
- Empty selection is a normal case, not missing analysis. Completeness still
  requires exactly one result for every selected specialist.
- Apply the additive `PerRunSpecialistSelection` migration and deploy API,
  worker, and client together. Finish active orchestrations before deployment.
- Prompt outputs are model-generated planning evidence, not proof of tests
  being written, executed, or signed off.
