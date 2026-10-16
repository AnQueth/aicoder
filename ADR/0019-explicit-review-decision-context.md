# 0019: Make review targets and consequences explicit

**Status:** Accepted

## Context

The live Vue walkthrough showed that generic "Human decision" and "Record
decision" labels made it difficult to tell which artifact feedback applied to.
Architecture history was displayed separately from its decision. Reviewer
responses and final plan approval looked similar despite different effects.

## Decision

Keep the existing workflow and API contracts, but make each review action name
its document, exact revision, and consequence. Link each form to its document.
Show the current architecture analysis beside its decision, selected by the
run's exact revision rather than array order. Disable decisions when their
target document is unavailable. Keep histories collapsed and label them as
reference material, not approval requests.

Distinguish a named reviewer's plan response from final authorization to
generate code. Explain that reviewer feedback alone does not regenerate a plan,
and that approval does not merge or deploy code. Show the current task above
the documents so users know where to focus.

## Consequences

- Feedback and approval remain revision-bound and follow existing review gates.
- Users can read the target and effect before submitting, without learning API
  stage names or interpreting historical artifacts.
- Backend behavior, authorization, and the fixed stage order are unchanged.
- This does not add a return-to-BA action, revision comparison, or new approval
  gate. Missing documents require a successful refresh, not blind approval.
