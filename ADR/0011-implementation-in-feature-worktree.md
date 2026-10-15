# 0011: Implement in a feature worktree with SDK built-ins

- **Status:** Accepted
- **Supersedes:** [ADR 0010](./0010-scoped-repository-workspace-tools.md)

## Context

The in-memory candidate-change tools let the implementation agent propose file
operations, but do not let it use the Copilot SDK's built-in repository and
shell capabilities. The requested workflow is for implementation to happen in
a real feature branch/worktree, while keeping the source/default branch
unchanged and retaining validation and human review before publication.

The Copilot SDK's working directory is not an operating-system sandbox. Built-in
tools, including shell execution, may access other files and resources available
to the service identity.

## Decision

After the existing human approvals, create a temporary checked-out worktree at
the run-pinned commit on the unique `collaborator/feature-{runId}` local branch.
Set only the implementation SDK session's working directory to this worktree
and enable all SDK built-in tools there. Approve each SDK permission request
once for that implementation session. Other Copilot stages remain read-only
and do not receive SDK built-ins.

Copilot makes edits directly on disk in the worktree. After the session, collect
Git changes, reject unsupported or unsafe paths and content, and apply the
existing patch limits and validation. Persist the validated changes and publish
the review branch through the existing workflow; do not change the source
branch or merge automatically. Dispose of the temporary worktree on success or
failure.

Allow the implementation session up to ten minutes, including SDK startup and
tool use. The previous three-minute analysis budget cancelled real code edits
before they could complete. Other stages keep their existing deadlines.

Return the run's captured repository URL with its run status. On successful
publication, the API client prints a review handoff even in quiet mode, including
the repository, branch, base and generated commits, changed paths, and PowerShell
commands to clone, inspect, and open a new review checkout. Print it last so it
does not get buried in plan history. Do not create a checkout or run Git on the
user's machine automatically, and do not present the removed temporary worktree
as an available review folder.

## Consequences

- Implementation can use the Copilot SDK's full built-in tool set in the
  feature worktree, including shell and file tools.
- Edits happen on disk in a real checked-out local branch; they are not held in
  the repository-source tool's in-memory candidate map.
- The original source/default branch is unchanged. Only validated changes are
  published to the unique human-review branch.
- All SDK permission requests from the implementation stage are approved once.
  This is a significant trust boundary: the worktree provides a useful starting
  directory but does not restrict access outside it. The service identity's
  filesystem and other capabilities remain accessible to built-in tools.
- Repository inspection tools remain read-only for non-implementation stages.
