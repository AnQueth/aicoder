# 0009: Explore pinned repositories on demand

- **Status:** Superseded by [ADR 0010](./0010-scoped-repository-workspace-tools.md)
- **Date:** 2026-10-14

## Context

AI workflow stages need to understand the product repository to produce
grounded specifications, architecture reviews, developer analyses, cross-role
synthesis, plans, and implementation proposals. The previous approach loaded an
allowlisted subset of files into an in-memory snapshot with fixed file-count
and content-size limits. This could prevent the AI from finding relevant code
in larger repositories or reading a complete file.

## Decision

For each repository-aware AI activity, including cross-role synthesis, open a
temporary, read-only clone at the run's pinned Git commit. Give the AI only
three repository tools: list eligible tracked text paths, search source lines,
and read a complete eligible file. Do not include the repository inventory or
raw source corpus in the model prompt. Keep path exclusions for secrets,
generated directories, and known binary file types. Dispose of the temporary
clone when the activity finishes.

## Consequences

- AI stages can discover relevant files and read them as needed, without
  application-level repository file-count or source-content limits.
- The model receives repository text only through tool results; the selected
  model's own context window still applies.
- Repository access stays read-only and tied to the same commit used by the run.
- Catalogs are versioned so cached results from the bounded index are rebuilt
  under the on-demand inspection approach.
- Each activity opens its own temporary clone, adding Git setup time and local
  disk use. Normal completion and cancellation dispose the clone; an abrupt
  process termination may leave a temporary directory behind.
- Secrets and binary detection depend on path and extension exclusions, so
  repositories should not contain credentials in ordinary source files.
