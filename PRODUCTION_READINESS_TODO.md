# Production readiness TODO

Status: planning checklist, not evidence of production readiness.
Created: 2026-10-16.

The application currently supports local development and live demonstration
workflows. The API deliberately refuses non-Development startup. Do not bypass
that guard or expose the shared-key development stack publicly to meet a release
deadline.

## Focus and release scope

First target: a controlled internal production pilot for explicitly authorized
users and repositories. Confirm that scope before implementation; public or
multi-tenant access requires additional isolation and identity design.

Keep the first release branch-only: generating a review branch must not grant
permission to merge or deploy its code.

The Vue website is a sample/reference client, not the required production UI.
Production scope centers on the API, workflow services, and repository execution.
Preserve the sample for development and contract demonstrations; deploying it is
optional and must not block a headless production release.

Work through the priorities below. Record owners, tracked issues, and verification
evidence as work is scheduled. Unchecked items are not implemented or verified.
Record consequential design choices in [ADR](ADR/README.md) when they are made.

### Selected deployment direction

Target **Azure Container Apps Environment**, not Azure App Service, as recorded
in [ADR 0021](ADR/0021-azure-container-apps-deployment-target.md).

- API container exposing the production API. The sample Vue site is optional.
- Separate workflow container with a compatible Durable Functions host.
- External persistent PostgreSQL and Durable workflow storage; no production
  dependency on the local Aspire emulators.
- Repository downloads into bounded, disposable per-run workspaces with scoped
  Git credentials and explicit cleanup.
- Isolated implementation/verification execution remains required production
  work. Container Apps Jobs is a candidate, not yet a selected implementation.

The platform direction is selected; runtime compatibility, identity, networking,
scaling, capacity, and execution isolation are not yet implemented or verified.

## P0 - Required before exposing the application to production users

### 1. Establish the deployment and trust boundaries

- [ ] Confirm users, approved repositories, data classification, region,
  concurrency, budget, availability target, and recovery objectives.
- [x] Record Azure Container Apps Environment as the target application platform.
- [ ] Finalize the Container Apps topology and supported Functions hosting model
  for the API, workflow host, and isolated implementation jobs.
- [ ] Validate .NET 10, Durable Functions, Copilot SDK authentication/licensing,
  subprocess execution, activity durations, and required repository toolchains
  on that hosting platform.
- [ ] Define which services may access source code, database records, model
  inputs/outputs, secrets, and repository write credentials.

Done when: an accepted deployment/trust-boundary ADR and a staging proof of
compatibility exist. Hosting choices must be based on actual runtime requirements,
not merely successful local Aspire execution.

### 2. Implement identity and server-side authorization

- [ ] Add production user sign-in and validated API authentication.
- [ ] Define product/repository access and permissions for configuration,
  prompt changes, reviewer assignment, feedback, approval, and implementation.
- [ ] Bind reviewer actions and audit actors to the authenticated identity.
  Do not trust a caller-supplied developer ID as proof of identity.
- [ ] Enforce permissions on every relevant API route, including directory,
  artifact, history, and event reads.
- [ ] Authenticate and authorize internal API-to-workflow calls independently
  of browser users; support credential rotation.
- [ ] Update the automated API client and document supported authentication for
  production API consumers.
- [ ] If the sample Vue site is deployed against production, integrate the
  selected authentication flow, including session expiry and safe sign-out.
- [ ] Retain an explicit local-development mode. Replace the production startup
  refusal only after production authentication is complete and tested.

Done when: tests prove anonymous and unauthorized access is denied, one reviewer
cannot impersonate another, and authorized users can complete the workflow.

### 3. Isolate AI implementation and verification

- [ ] Execute generated commands in disposable, restricted job environments,
  separate from the API and orchestration service.
- [ ] Prevent jobs from reaching host files, infrastructure administration
  credentials, unrelated repositories, and workload identity endpoints.
- [ ] Restrict network egress and repository credentials to the required scope.
  Separate untrusted build/test execution from credentialed branch publication.
- [ ] Apply CPU, memory, disk, concurrency, and execution-time limits; support
  cancellation and reliable workspace/process cleanup.
- [ ] Validate repository URLs and network destinations; define production
  handling of local file sources, private networks, and Git submodules/hooks.
- [ ] Treat repository files, tool output, and submitted prompts as untrusted
  input. Test that they cannot expand tool permissions or bypass approval gates.

Done when: isolation tests demonstrate jobs cannot access other jobs' state,
service secrets, or unauthorized destinations. A Git worktree alone is not an
acceptable isolation boundary.

### 4. Move configuration and data services to production infrastructure

- [ ] Provision PostgreSQL with TLS, restricted access, backups, and restore
  procedures; use separate staging and production databases.
- [ ] Replace Azurite with supported production Durable storage. Use separate
  task hubs/storage configuration for each environment.
- [ ] Replace the Key Vault emulator with a production secret store and
  least-privilege workload identity. Remove emulator-specific credential/client
  relaxations from the production path.
- [ ] Configure real workflow service discovery/addresses and protected
  service-to-service networking.
- [ ] Configure Copilot authentication and scoped Git repository credentials
  without interactive developer-machine dependencies.
- [ ] Define source/transcript/artifact retention, deletion, access, and backup
  policies. Confirm authorization to send repository content to the AI service.
- [ ] Validate required settings at startup without logging secret values.

Done when: staging runs without user secrets, local filesystem remotes, emulators,
or a developer's cached Git/Copilot login.

## P1 - Required for a controlled production pilot

### 5. Make packaging and deployment repeatable

- [ ] Add reviewed infrastructure as code and environment-specific configuration.
- [ ] Add release packaging for the API, Functions worker, migrator, and isolated
  execution jobs, including required Git/Copilot/repository tooling.
- [ ] Make production API packaging independent of the sample Vue site and
  exclude sample assets by default. If explicitly included, build Vue before
  publishing the API; ship compiled assets, not Vite.
- [ ] Create CI gates for frontend/backend builds and tests, dependency and
  secret scanning, and deployment-artifact validation.
- [ ] Resolve or explicitly disposition the known source-search test failure
  and unavailable vulnerability-metadata checks. Do not silently exclude them
  from release confidence.
- [ ] Deploy immutable, identifiable artifacts to staging, then promote those
  same artifacts with an explicit production approval.
- [ ] Run database migrations as a controlled release job with compatibility
  and rollback/forward-recovery procedures.

Done when: a clean CI agent can build and deploy staging without manual machine
setup, and a release records artifact versions, migrations, checks, and approver.

### 6. Prove workflow durability and safe upgrades

- [ ] Test restarts during specification/architecture review, proposal collection,
  planning, implementation, and branch publication.
- [ ] Verify duplicate requests/events, outbox retries, concurrent confirmations,
  and stale revisions cannot skip gates or produce unintended duplicate work.
- [ ] Define retry, timeout, cancellation, failed-run recovery, and operator
  intervention behavior without silently reporting success.
- [ ] Test an application upgrade with in-progress Durable instances and database
  records. Preserve compatible orchestration history or version workflows.
- [ ] Test worker scale-out and enforce per-run/repository execution ownership.
- [ ] Reconcile interrupted Git publication without overwriting an unrelated
  branch or changing the source/default branch.

Done when: fault-injection tests and a staged upgrade show saved decisions survive,
work resumes safely, and recovery is documented.

### 7. Add independent generated-code acceptance gates

- [ ] Define required repository-specific build, test, and validation commands.
- [ ] Run verification independently from the implementer's self-reported result,
  within the isolated execution boundary.
- [ ] Store command outcomes, commit identifiers, and reviewable evidence.
- [ ] Make failed, missing, cancelled, and unsupported checks explicit.
- [ ] Distinguish branch publication, verification passed, human acceptance, and
  release authorization in API/UI/console status.
- [ ] Keep merges and deployments disabled unless separately designed and
  authorized.

Done when: an intentionally broken generated change cannot be labeled verified
or release-ready, even if its branch was successfully published.

### 8. Add operations, observability, and recovery

- [ ] Expose appropriate production readiness/liveness checks; do not rely on
  the current development-only health mappings.
- [ ] Collect correlated API, workflow, job, database, and model-call telemetry.
  Redact credentials and minimize source/personal data in logs.
- [ ] Alert on failed/stuck runs, outbox backlog, storage/database errors,
  authentication failures, resource exhaustion, and AI usage thresholds.
- [ ] Add admission controls, per-user/product quotas, and concurrency limits.
- [ ] Define audit retention and protect audit records from ordinary user edits.
- [ ] Write runbooks for failed jobs, credential rotation, outage recovery,
  rollback, and database/Durable-state restoration.
- [ ] Perform a restore drill and measure recovery against the agreed objectives.

Done when: an operator can detect a failed run, identify its cause, recover it
without losing approvals, and restore the environment from documented backups.

## P2 - Required before expanding beyond the pilot

- [ ] Run realistic load/soak tests with large repositories and concurrent runs;
  size compute, disk, database, storage, and model usage from measurements.
- [ ] Ensure production consumers can identify the exact artifact/revision being
  approved. If a browser client is included, review accessibility, browser
  compatibility, and session-expiry UX.
- [ ] Complete an independent security review and remediate release blockers.
- [ ] Review organizational AI, licensing, privacy, and source-code handling
  requirements; obtain the required approvals.
- [ ] Expand isolation and authorization tests before enabling additional teams,
  repositories, or tenants.
- [ ] Automate infrastructure drift detection and regularly rehearse upgrades
  and disaster recovery.

## Release acceptance checklist

- [ ] P0 and P1 items have owners, completion evidence, and accepted outcomes.
- [ ] Production starts in `Production`, with real user authorization and no
  shared development-key fallback.
- [ ] Both developer-proposal modes pass an end-to-end staging scenario using
  production-style identity, storage, networking, and isolated execution.
- [ ] An approval wait survives restart and an upgrade.
- [ ] A failed code verification and an unauthorized approval are both blocked.
- [ ] Repository default branches remain unchanged by the workflow.
- [ ] Backups, restore drill, monitoring, runbooks, and release rollback are ready.
- [ ] A named owner signs off the pilot scope and residual risks.

## Recommended first work package

1. Confirm pilot scope and deployment/trust boundaries.
2. Implement authenticated reviewer identity and route-level authorization.
3. Build an isolated implementation/verification job proof of concept.
4. Validate that proof of concept on the selected hosting platform.
5. Then package and automate staging deployment.

Do not prioritize infrastructure provisioning over identity and execution isolation:
hosting the current development application does not make it production-safe.

## Supporting documentation

- [Development guide](Code/README.md)
- [Verification history and known limitations](Code/TEST_PROGRAM.md)
- [Current component diagram](COMPONENT_DIAGRAM.md)
- [Existing implementation plan](IMPLEMENTATION_PLAN.md)
- [Architecture decisions](ADR/README.md)
