# Repository Guidelines

## Code comments

- Write comments in plain, ELI5-style language so a new contributor can understand
  the intent without needing to decode jargon.
- Explain why non-obvious logic exists, important constraints, and surprising
  behavior. Do not add comments that merely repeat what the code already says.
- Update or remove comments when the related code changes.

## Architecture decision records

- Record meaningful architectural, technical, and product-design decisions in
  [`ADR/`](../ADR/README.md), including the context, the chosen option, why it
  was chosen, and the consequences.
- Add an ADR when making a decision that affects system structure, interfaces,
  data, dependencies, security, or future implementation direction. Keep the
  record with the change that introduces the decision.
