# 0001: Plain-language comments and decision records

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

The repository needs shared conventions that make code easier for new
contributors to understand and preserve the reasoning behind significant
decisions.

## Decision

Write comments for non-obvious code in clear, ELI5-style language. Comments
should explain intent, constraints, or surprising behavior rather than restate
syntax.

Record significant architectural, technical, and product-design decisions in
numbered ADR files under `ADR/`, capturing the context, rationale, and
consequences.

## Consequences

Code review should check that useful comments are understandable and stay
accurate as code changes. Significant decisions gain a discoverable history;
small local implementation choices do not require a separate ADR.
