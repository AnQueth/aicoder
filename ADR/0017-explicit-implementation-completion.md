# 0017: Require explicit implementation completion

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

A live subtraction-feature demonstration returned a structured progress check-in
after installing Knockout.js, before implementing the arithmetic or UI. The
workflow accepted any nonempty summary and published that partial patch.

## Decision

Require a boolean `complete` alongside the implementation summary. A false value
logs progress and resumes the same session, worktree, and approved plan. Only a
true value permits patch collection. Missing or invalid fields fail explicitly.
The existing overall ten-minute cancellation deadline covers all continuation
turns; expiration aborts the session and cannot publish partial success.

## Consequences

Progress messages no longer end implementation accidentally. This is an explicit
model completion contract, not independent proof of acceptance criteria or passing
tests; published branches still require verification and human review.
