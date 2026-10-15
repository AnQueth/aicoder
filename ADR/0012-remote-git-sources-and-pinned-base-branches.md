# 0012: Remote Git sources and pinned base branches

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

The client accepted remote repository URLs, but the API and workflow allowed
only local file repositories. Repository snapshots always started at the default
branch, so products using another main development line could not choose it.
Runs already pin their source commit, and that guarantee must remain intact.

## Decision

Support HTTPS, SSH, and local file Git URLs throughout intake, analysis,
implementation, and review-branch publication. Keep credentials outside product
configuration: Git uses the workflow host's credential helper or SSH agent and
known-hosts configuration. Allow SSH usernames but reject embedded passwords,
HTTPS user information, query strings, and fragments. Disable interactive Git
credential prompts and retain the existing command timeout.

Add nullable `repositoryBaseBranch` to product registration and repository
updates. Expose it as `--base-branch` and `COLLABORATOR_PRODUCT_BASE_BRANCH`.
Null means the repository's default `HEAD`, preserving previous behavior.
Repository updates replace the URL and branch together and invalidate the
product catalog cache. A run copies both fields at submission.

Use one shared download helper that initializes a temporary Git repository and
shallow-fetches the exact `refs/heads/<branch>`, or `HEAD` when unspecified.
Explicit branch refs avoid accidentally choosing tags. Missing branches fail;
there is no fallback. Once pinned, every later download fetches the exact commit
without resolving the branch again. Implementation and publication remain based
on that commit and never advance the configured base branch.

Preserve the current `InitialCreate` baseline and add a nullable-column migration
for existing databases. This follows the post-baseline migration provision in
[ADR 0006](./0006-single-current-database-baseline.md); historical pre-baseline
databases remain unsupported.

## Consequences

- Products can use `main`, `master`, `develop`, or another branch without renaming it.
- Product reconfiguration and moving branch tips cannot change in-flight runs.
- Hosted Git access and publication need network connectivity and host-provisioned
  read/write credentials. Credential provisioning and hosted pull requests are
  not implemented. SCP-style SSH addresses are not accepted; use an `ssh://` URL.
- Remote availability, authentication failures, and command timeouts fail the
  workflow explicitly. Large repositories may exceed the existing timeout.
- Local fixture URL rewriting tests the remote URL paths without sending source
  code or credentials to any external service; live authentication is not covered.
