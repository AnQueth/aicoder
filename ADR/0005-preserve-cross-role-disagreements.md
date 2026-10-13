# 0005: Preserve cross-role disagreements before planning

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

Each captured developer skill now produces a separate analysis, but planning still
has to compare the perspectives. Passing separate outputs alone does not make
agreement, conflict, or missing decisions easy to review. A synthesis could also
hide a conflict or falsely imply that roles agreed.

## Decision

After all specialist analyses are persisted, run a separate cross-role synthesis
activity before planning. The synthesis must identify agreements, disagreements,
and unresolved questions, and link each point to the exact captured skill revisions
that support it. Validate those references against the run snapshot.

Keep the original specialist analyses unchanged and pass both the original results
and the synthesis to planning. The plan must preserve relevant conflicts and open
questions rather than silently choosing a side. Make the synthesis and source links
available through the API and console so humans can review them alongside the plan.
The synthesis is advisory; existing human approval gates remain authoritative.

Make synthesis and collaborative planning part of the single canonical Durable
workflow. This is a new product, so do not retain older orchestration or activity
implementations solely to replay their histories. Ensure no active instances
depend on an older workflow before deploying changes to the activity sequence.

## Consequences

- Cross-role agreements, conflicts, and questions are explicit and traceable.
- Synthesis output cannot introduce references to uncaptured skill revisions.
- The planner sees all original role findings as well as the synthesis.
- Human reviewers have the synthesis before deciding on the plan.
- The added AI activity increases latency and model usage; offline mode clearly
  marks comparison as not performed.
- Older Durable histories are not guaranteed to replay against changed workflow
  code; deployments must not leave active runs on an older implementation.
- The synthesis remains model-generated advice and must not replace human review
  or the original specialist evidence.
