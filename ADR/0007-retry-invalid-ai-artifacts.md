# 0007: Retry invalid AI artifacts once with validation feedback

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

AI-generated business specifications, architecture assessments, and plans must
pass strict application validation before they can move through the workflow.
Structured output reduces formatting mistakes, but generated content can still
miss required lists or cite repository paths that were not inspected. Previously,
one invalid response immediately failed the entire workflow, even when the
validator could explain what needed correction.

## Decision

For business specifications, architecture assessments, and plans, validate each
AI response before returning it to the workflow. If the response is malformed or
fails its contract, make one additional request in the same bounded Copilot
session with the validator's fixed error message and the original task context.
Validate the corrected response again. If it is still invalid, fail the workflow;
never store or advance an invalid artifact.

## Consequences

- Temporary output-shape mistakes can be corrected without restarting a run.
- Validators provide field-specific errors so the correction request can target
  the missing or invalid requirement.
- Each affected stage may use one extra model call, increasing latency and usage.
- Persistent contract failures remain explicit failures rather than being
  hidden by a fallback or a success-shaped result.
