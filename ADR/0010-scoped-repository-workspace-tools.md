# 0010: Use scoped repository workspace tools

- **Status:** Superseded by [ADR 0011](./0011-implementation-in-feature-worktree.md)
- **Date:** 2026-10-14

## Context

On-demand list, search, and full-file reads let Copilot understand a complete
pinned repository, but the implementation agent could only return file
contents in a structured response. The desired workflow also needs Copilot to
make changes using tools, without giving an AI stage arbitrary filesystem or
shell access. Repository changes must remain subject to the existing human
approval gates and patch validation.

## Decision

Keep analysis, synthesis, cataloging, and planning tools read-only. After a
plan is approved, give the implementation stage workspace-scoped list, literal
search, regular-expression search, read, create/update, and delete tools. The tools operate on an isolated,
temporary candidate change set tied to the run's pinned Git commit. They reject
excluded paths, invalid operations, binary content, and patches exceeding the
existing file and content limits. The code-change service validates the
resulting patch and is still the only path to a review branch.

Do not enable Copilot SDK built-in tools, MCP tools, arbitrary shell commands,
or general filesystem access. This deployment does not provide an OS-level
sandbox that would safely contain those capabilities.

## Consequences

- Copilot can inspect and edit repository files through codebase-defined tools
  registered through the Copilot SDK after
  human plan approval.
- Changes remain an isolated candidate patch; tool calls do not modify the
  user's source repository.
- Other AI stages cannot write files, and no stage can run commands or tests.
- Application-level path and patch validation remains in force before branch
  creation. Human review remains required; branches are not merged automatically.
- Tool writes are captured as structured file operations, so they remain
  auditable and can be validated independently of the model's final summary.
