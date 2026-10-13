# 0006: Start databases from one current schema baseline

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

The product is new and its database schema is still evolving. Keeping the
intermediate migrations created during development adds history that deployed
customers do not need and can imply that old schemas are supported. The previous
skill-specific business-analyst table is also unused; current skills are stored
in the shared role/domain skill registry.

## Decision

Keep one EF Core `InitialCreate` migration generated from the current
`CollaboratorDb` model. Do not preserve upgrade steps from earlier development
schemas or migrate their data. A deployment must start with an empty database.
Remove unused persistence entities rather than retaining their tables in the
baseline. The local AppHost uses a new named PostgreSQL volume for this baseline
and leaves the previous development volume untouched.

## Consequences

- New environments create the complete current schema with one migration.
- Existing development databases are not upgraded or deleted; the local AppHost
  starts with a new, empty volume, and old local data remains in its prior volume.
- Any future deployed schema change must be introduced as a migration from this
  baseline.
- Existing business-analyst skill data from earlier schemas must be recreated in
  the current generic skill registry; no automatic import is provided.
