# Architecture Decision Records

Store this repository's architecture and significant technical decision records
in this directory. Name each record `NNNN-short-title.md`, using the next
available number.

Each record should include:

- **Status** — proposed, accepted, superseded, or deprecated.
- **Context** — the problem, constraints, and options considered.
- **Decision** — what was chosen and why.
- **Consequences** — expected benefits, trade-offs, and follow-up work.

Update the relevant ADR when a decision is superseded; do not silently rewrite
the historical rationale.

Current decisions:

- [0001: Plain-language comments and decision records](./0001-plain-language-comments-and-decision-records.md)
- [0002: Immutable skill revisions and stage artifact provenance](./0002-skill-revisions-and-stage-artifacts.md)
- [0003: Require human-reviewed architecture before planning](./0003-human-reviewed-architecture-stage.md)
- [0004: Process every captured developer skill](./0004-process-captured-developer-skills.md)
- [0005: Preserve cross-role disagreements before planning](./0005-preserve-cross-role-disagreements.md)
- [0006: Start databases from one current schema baseline](./0006-single-current-database-baseline.md)
- [0007: Retry invalid AI artifacts once with validation feedback](./0007-retry-invalid-ai-artifacts.md)
- [0008: Route workflow calls through the Functions HTTP host](./0008-functions-host-http-endpoint.md)
