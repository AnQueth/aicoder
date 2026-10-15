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
- [0009: Explore pinned repositories on demand](./0009-on-demand-pinned-repository-exploration.md)
- [0010: Use scoped repository workspace tools](./0010-scoped-repository-workspace-tools.md)
- [0011: Implement in a feature worktree with SDK built-ins](./0011-implementation-in-feature-worktree.md)
- [0012: Remote Git sources and pinned base branches](./0012-remote-git-sources-and-pinned-base-branches.md)
- [0013: Maintain one current API contract](./0013-single-current-api-contract.md)
- [0014: Allow all repository file types without application file caps](./0014-unrestricted-repository-file-changes.md)
- [0015: Use a QA specialist in developer planning analysis](./0015-qa-specialist-during-planning.md)
- [0016: User-defined prompts and per-run specialist selection](./0016-user-defined-per-run-specialists.md)
- [0017: Require explicit implementation completion](./0017-explicit-implementation-completion.md)
