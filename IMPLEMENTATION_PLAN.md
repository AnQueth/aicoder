# Collaborator Implementation Plan

**Status:** Approved direction; foundation implementation underway  
**Date:** 2026-10-04  
**Source:** [SPECIFICATION.md](./SPECIFICATION.md) and the subsequent requirements discussion.

## 1. Objective and agreed requirements

Build a headless application-development platform that accepts a requested feature as text through a REST API and processes it against configured product repositories.

The implementation will use:

- C# targeting .NET 10 (`net10.0`).
- Microsoft Agent Framework for specialized agents.
- Azure Durable Functions for persistent workflow processing.
- .NET Aspire for local resource orchestration, service discovery, observability, and one-step F5 startup.
- PostgreSQL for application/business data, including configuration revisions, artifacts metadata, approvals, and the transactional outbox.
- A REST API for every client-facing and administrative operation; no UI is required.
- Versioned configuration for products, repositories, developers, roles, agent definitions, skills, and workflow policies.
- Repository read credentials referenced per repository and stored securely, never embedded in configuration.
- Required human contributions and approvals submitted through the API.
- Traceable, revisioned artifacts from request through validation and release.

The initial operating policy retains the specification's human review requirements. Disabling plan approval must not implicitly disable collaboration or final-output validation.

### Implementation checkpoint

The [Code foundation](Code/README.md) now builds on .NET 10 and runs locally with
Aspire, PostgreSQL, Azurite, Durable Functions, and Microsoft Agent Framework.
Its implemented slice is product-brief registration, text submission, draft
generation, revision-bound manual approval/rejection, audit, and outbox delivery.
Eight unit tests and fourteen live local HTTP checks pass. Full AppHost restart
recovery of a pending approval has also been verified.

This is not yet the repository-grounded milestone: repository configuration and
read-token resolution, indexing, developer/role assignments, alerts, collaboration,
coding workers, and releases remain pending. Azure OpenAI live calls and interactive
F5 debugging remain unverified; the default tested profile is explicitly Offline.

## 2. Context map and scope

| Existing file | Purpose | Planned treatment |
|---|---|---|
| [SPECIFICATION.md](./SPECIFICATION.md) | Product requirements and acceptance scenarios AC-01 through AC-17 | Update agreed technology, Aspire/F5, PostgreSQL, API, configuration, and credential requirements during foundation implementation. |
| This plan | Implementation sequence and verification gates | Use as the execution baseline after approval; revise explicitly if decisions change. |

At planning time the workspace contained no application code, dependency manifests,
tests, or deployment files. The greenfield foundation now lives under [Code](Code/).
This is not a framework upgrade.

No source changes, package installations, cloud provisioning, credentials, or deployments are performed by writing this plan.

### Initial boundaries

- Pilot one product with one or two repositories.
- Implement one source-control provider first, selected before integration work.
- Keep manual reviews enabled.
- Complete a real text-to-reviewed-pull-request pilot before adding production deployment.
- Support both existing-product baselines and a reviewed product brief for products without repositories.
- Defer a UI, additional source-control providers, unrestricted parallel coding, and automatic autonomy promotion.

## 3. Proposed architecture

See the [Mermaid component diagram](./COMPONENT_DIAGRAM.md) for component relationships, local Aspire resources, and execution boundaries.

```text
CLI / HTTP client / future UI
              |
       ASP.NET Core REST API
              |
   PostgreSQL + transactional outbox
              |
   Azure Durable Functions isolated worker
              |
       Stage activities / sub-orchestrations
        /            |                 \
Repository       Agent Framework    Notifications,
analysis         stage agents       human waits,
workers                              CI integrations
        \            |                 /
         Versioned artifacts and audit records
                     |
        Isolated coding/build job runner
                     |
       Branch / pull request / CI / release
```

### Proposed solution projects

| Project | Responsibility |
|---|---|
| `Collaborator.AppHost` | Aspire startup project coordinating local services, PostgreSQL, storage emulation, and Durable infrastructure. |
| `Collaborator.ServiceDefaults` | Shared service discovery, health checks, and OpenTelemetry for compatible hosts; verify Functions-host integration separately. |
| `Collaborator.Core` | Domain records, contracts, stage rules, policy evaluation, and artifact metadata. |
| `Collaborator.Infrastructure` | Database persistence, outbox, artifact storage, credentials, and external adapters. |
| `Collaborator.Agents` | Agent Framework integration, definition loading, context assembly, and structured output validation. |
| `Collaborator.Api` | Authentication, authorization, REST endpoints, OpenAPI, and client-facing errors. |
| `Collaborator.Workflows` | Durable orchestrations, activities, timers, event handling, and job coordination. |
| `Collaborator.Runner` | Isolated repository analysis, implementation, build, and test job execution. |
| Test projects | Unit, contract, integration, durable recovery, and end-to-end tests. |

These are boundaries within one solution, not a mandate to build independent microservices.

### Storage and hosting

- PostgreSQL locally and in deployed environments for business records, transactions, concurrency, and the outbox; use EF Core with the Npgsql provider and versioned migrations.
- Aspire-managed PostgreSQL with a persistent development volume and generated development credentials; do not use an in-memory database as a substitute for PostgreSQL integration tests.
- Blob storage for large artifacts, evidence, and logs.
- Azure Key Vault for repository credentials and provider secrets.
- A supported Durable Functions backend for orchestration history; choose the backend in the compatibility spike.
- Azurite for local Azure Storage dependencies, plus the Durable Task Scheduler emulator if the chosen backend requires it.
- Aspire manages all required local infrastructure. Prefer official emulators for Azure services and real local containers for databases; the default development profile must not require provisioned Azure resources.
- ASP.NET Core API hosting and a separate Functions worker deployment.
- Dedicated ephemeral execution environments for coding and builds; verify their sandbox boundary before running untrusted repository commands.

Business records remain authoritative for approvals and artifacts. Durable history is authoritative for workflow execution progress. Neither replaces the other.

PostgreSQL is the application database, not an assumed Durable Functions history provider. Aspire coordinates local resources; it does not replace Durable orchestration or provide a security sandbox.

### F5 local development contract

- Set the C# Aspire AppHost as the startup project and provide checked-in Visual Studio/VS Code launch support.
- After documented one-time prerequisites and secret setup, a single F5 starts PostgreSQL, local storage/Durable infrastructure, the REST API, Functions host, and the runner coordination service.
- Use the supported Aspire Azure Functions integration where compatible; verify debugger attachment and required Core Tools rather than assuming ordinary project hosting starts a Functions host correctly.
- Wire connections and dependency readiness through AppHost references and health checks; run a single migration resource before database-dependent services become ready.
- Expose API endpoints, resource health, logs, and traces through the Aspire dashboard. The dashboard is development tooling, not a product UI requirement.
- Persist local database and workflow state across ordinary restarts; document explicit development-only reset procedures.
- Provide an explicitly selected offline profile with model and notification test doubles so infrastructure/workflow development needs no live model or messaging account. Live mode must fail clearly when credentials are missing, never silently switch to doubles.
- Keep development credentials in supported user-secret stores. There is no assumed Key Vault emulator; local credential resolution uses an explicitly development-only secret provider, with Key Vault retained for deployed use and live verification.
- Any third-party Key Vault-compatible emulator requires explicit selection and compatibility/security review before adoption. It must be development-only, and its tests do not establish actual Azure Key Vault compatibility.
- Runner coordination can start under Aspire, but arbitrary repository builds/coding still require the approved isolated job environment.
- Verify the compatible .NET SDK, Aspire tooling, container runtime, Azure Functions Core Tools, and editor/debug extensions as prerequisites.

### Aspire local infrastructure inventory

| Dependency | Local resource | Verification boundary |
|---|---|---|
| Application database | Real PostgreSQL container with persistent development volume | Exercises PostgreSQL behavior, EF Core/Npgsql mappings, migrations, and transactions. |
| Blob/queue/table storage used by the application or Functions | Official Azurite emulator | Exercises the supported local Azure Storage APIs; live-service verification remains separate. |
| Durable workflow backend | Azurite for the Azure Storage provider, or the Durable Task Scheduler emulator when that backend is selected | Use the backend proven compatible with the Agent Framework integration; do not start an unused scheduler emulator. |
| Repository/provider secrets | Development-only secret provider backed by user secrets | No official Azure Key Vault emulator is assumed. Keep production Key Vault integration behind the same application credential interface. |
| Notifications | Local capture adapter exposed through authenticated development API endpoints | Captures actual notification requests without sending messages to people. |
| Model calls | Explicit offline test double, or a configured live provider | Offline mode verifies workflow behavior, not model quality. Missing live credentials remain an error. |

Start resources through the AppHost, including any emulators needed by the chosen integrations. Azure managed identity, Key Vault authorization, and other cloud-only behavior require separate authorized live tests.

## 4. Decisions needed before their implementation phases

| Decision | Proposed approach | Needed before |
|---|---|---|
| .NET and package versions | .NET 10 is selected. Pin an exact supported .NET 10 SDK in `global.json`; verify Aspire, Functions isolated worker, Agent Framework, EF Core, and Npgsql compatibility and pin package versions after a spike. Any incompatibility is an explicit blocker, not permission to silently downgrade .NET. | Phase 1 |
| Agent package release status | Obtain explicit approval if required packages are prerelease. Do not silently install preview dependencies. | Phase 1 |
| Model provider and deployment | Select an approved provider/model, credentials, and data-use policy; keep model references configurable. | Phase 1 agent spike |
| Source-control provider | GitHub or Azure DevOps first, based on the pilot repositories. | Phase 4 |
| API identity | Microsoft Entra ID with application roles and authenticated participant identities. | Phase 3 |
| Persistent storage | PostgreSQL is selected; confirm hosting, artifact store, and the separate Durable backend. | Phases 1-2 |
| Notification delivery | One approved email or messaging adapter, plus API-readable input requests. | Phase 6 |
| Engineering policy | Eligible pool, collaborator count, deadline, availability, and disagreement escalation. | Phase 6 |
| Execution environment | Approved isolation, network policy, resource limits, and permitted tools. | Phase 8 |
| Deployment target | Pilot staging target, approval authority, health checks, and recovery procedure. | Phase 10 |

Undefined "SI" does not block the selected stack, but its intended meaning should be resolved in the specification.

## 5. Configuration, credentials, and skills

### Configuration model

Use a versioned JSON configuration document with a schema and an API import operation. JSON avoids introducing a separate parser for the first version.

The document contains:

- Products and repository relationships.
- Repository URLs, branches, source-control provider, indexing scope, and read credential references.
- People, authenticated identities, roles, eligible products/repositories, and notification destinations.
- Assignment rules, approval rules, deadlines, retries, and execution budgets.
- Agent definitions, model references, skill revisions, and tool-policy identifiers.

**Authority:** API-managed configuration revisions are authoritative at runtime. A file is a validated import/export representation, not an independently mutable second source of truth.

Run creation pins a configuration revision. Subsequent edits do not silently change running workflows. Safety-related revocations must still take effect before privileged actions.

### Repository credentials

- Every private repository identifies its read credential reference.
- Multiple repositories may deliberately share a credential.
- Credential registration and replacement accept a secret through an authorized write-only API.
- Store the token in Key Vault; return only nonsecret metadata.
- Resolve secrets inside the repository worker/activity, never inside an orchestrator or prompt.
- Do not place tokens in repository URLs, process command arguments, logs, HTTP traces, artifacts, or durable history.
- Validate repository and branch access; block work visibly on missing, expired, or insufficient credentials.
- Use separate narrowly scoped write credentials for branch and pull-request publication.
- Repository URLs must be validated against permitted providers/hosts to prevent arbitrary network access.

### Agent definitions and skills

Agent definitions declare responsibility, model reference, input/output schema, allowed skills, and tool-policy identifier. Markdown skill files contain task instructions, templates, examples, and constraints.

Initial roles:

- Product analyst: Discovery and feature clarification.
- Analysis agent: bounded work items and dependency graph.
- Architect: impact analysis, boundaries, and technical trade-offs.
- Planner: implementation plan from reviewed requirements and contributions.
- Developer: implementation in an authorized workspace.
- Validator: independent evidence review; cannot replace required human sign-off.

Role files do not enforce security. C# policy checks and actual tool permissions enforce it. Agents cannot create approvals or advance the workflow by asserting success.

## 6. REST API contract

All routes use `/api/v1`. Publish OpenAPI and example HTTP requests.

| Operation | Route |
|---|---|
| Import and validate configuration | `POST /configuration-imports` |
| Inspect/export a configuration revision | `GET /configuration-revisions/{id}` |
| Manage products and repositories | `/products`, `/products/{id}/repositories` |
| Manage participants and policies | `/people`, `/assignment-policies`, `/workflow-policies` |
| Register a repository read credential | `POST /products/{id}/repositories/{repositoryId}/read-credential` |
| Rotate or revoke repository read credentials | `PUT` or `DELETE` on the same credential resource |
| Start repository onboarding/refresh | `POST /products/{id}/baseline-runs` |
| Submit feature text and trigger processing | `POST /products/{id}/feature-requests` |
| Inspect a request and its disposition | `GET /feature-requests/{id}` |
| Inspect run status and blockers | `GET /runs/{id}` |
| Read paginated progress events | `GET /runs/{id}/events` |
| List artifacts and retrieve content | `GET /runs/{id}/artifacts`, `GET /artifacts/{id}` |
| Find outstanding human actions | `GET /input-requests` |
| Submit a required contribution or clarification | `POST /input-requests/{id}/responses` |
| Inspect and decide approvals | `GET /approval-requests`, `POST /approval-requests/{id}/decisions` |
| Request pause, resume, or cancellation | `POST /runs/{id}/commands` |
| Manage approved agent/skill revisions | `/agent-definitions`, `/skills` |

Feature submission accepts a product-scoped `text` field, with optional supporting references. It returns `202 Accepted`, identifiers, and a `Location` pointing to the run.

Require scoped idempotency keys for asynchronous submissions and side-effecting decisions. Reusing a key with a different payload is a conflict, not another run.

Use revision-aware updates, pagination, consistent Problem Details errors, request-size limits, and role/product-scoped authorization. Stale approvals or responses must not advance a newer artifact revision.

Expose stable public run states and blockers without leaking credentials, raw prompts, or internal Durable management URLs. Polling is sufficient initially; streaming and webhooks can be added later.

## 7. Implementation phases and verification gates

### Phase 1: Compatibility spike and solution foundation

**Implement**

- Target application projects to `net10.0`, pin the .NET 10 SDK, and confirm Aspire, Azure Functions isolated worker, Agent Framework, Durable integration, EF Core, and Npgsql compatibility.
- Create the solution, shared build settings, central package versions, and test projects.
- Create AppHost and ServiceDefaults projects; wire PostgreSQL, Azurite, the chosen Durable backend/emulator, API, Functions host, and runner coordination.
- Add one-step F5 launch configuration, health/readiness dependencies, development secrets, and explicitly selected offline/live profiles.
- Prove one structured agent invocation and a Durable wait resumed by an API-triggered event.
- Add deterministic test doubles for the model and external integrations.
- Update the specification with the agreed stack and API-first requirements.

**Verify**

- Clean restore/build succeeds.
- From a stopped environment, F5 starts all required local resources without separate terminal startup commands; verify API responsiveness, actual PostgreSQL access, and a running Functions host.
- Verify breakpoints in both the API and Functions worker, dashboard telemetry, and persisted state after stop/start.
- Live approved-model invocation succeeds when credentials are available.
- Workflow survives worker restart during a wait and completes after a valid response.
- Pin the versions demonstrated by the spike; record unsupported or preview capabilities.

### Phase 2: Persistence, artifact revisions, and reliable dispatch

**Implement**

- Products, repository registrations, configuration revisions, requests, runs, stages, artifacts, assignments, input requests, approvals, execution jobs, and audit events.
- EF Core/Npgsql mappings and PostgreSQL migrations, with an Aspire-managed local migration resource and a separate controlled deployment migration step.
- Explicit lifecycle transition rules and optimistic concurrency.
- Transactional outbox for orchestration starts, external events, and notifications.
- Idempotent dispatch and recovery when dispatch succeeds but acknowledgment persistence fails.
- Large artifact storage outside Durable history.

**Verify**

- Concurrent updates cannot overwrite an approval or response.
- A crash after persistence does not lose a request or event.
- Duplicate dispatch does not create duplicate runs or business side effects.
- Exact upstream revisions can be reconstructed for each artifact.
- Migrations and outbox/concurrency tests pass against real PostgreSQL, including restart persistence and competing dispatch workers.

### Phase 3: Authenticated REST API and configuration

**Implement**

- Headless endpoints, OpenAPI, input validation, Problem Details, and authentication.
- Role- and product-scoped access for contributors, engineers, approvers, and administrators.
- Configuration schema validation, import/export, revision activation, and agent/skill management.
- Asynchronous feature submission, status queries, event pagination, and credential metadata.

**Verify**

- A valid text request returns `202` and an inspectable queued run.
- Repeating a submission with the same key does not duplicate it.
- Unauthorized users cannot read another product's artifacts or approve its plans.
- All administrative and human actions are usable without a UI.

### Phase 4: Secure repository onboarding and evidence indexing

**Implement**

- Key Vault credential resolution, write-only registration/rotation, and access validation.
- Provider adapter, safe clone/fetch, pinned commit tracking, and cleanup.
- Bounded file inventory, text search, and C# symbol extraction for the pilot.
- Reviewed capability/architecture baseline, evidence references, gaps, and freshness metadata.
- Incremental refresh and detection of changed source evidence.
- Add semantic retrieval only if evaluation shows text/symbol retrieval is insufficient.

**Verify**

- Private repositories can be read with the configured credential.
- Revoked credentials produce a visible blocked result without secret leakage.
- Excluded, oversized, binary, and sensitive files are handled under explicit policy.
- Submodules, symlinks, and repository-supplied commands cannot escape ingestion boundaries.
- Generated claims point to repository, commit, path, and relevant source locations.
- Existing-product Discovery waits for a sufficient reviewed baseline.

### Phase 5: Discovery, Analysis, and durable stage processing

**Implement**

- Agent definition/skill loading, pinned versions, stage-specific context, and validated structured output.
- Text request to feature proposals, clarification requests, work items, and dependency graphs.
- Architect impact analysis against baseline evidence.
- Durable stage sequencing, bounded retries, budgets, explicit blockers, and dependency-ready scheduling.
- Rejection of cycles and unresolved blocking assumptions.

**Verify**

- Agent output is schema-valid and traceable to the original text.
- Unsupported product claims and missing evidence are surfaced.
- Transient failure retries are bounded; semantic errors do not become silent success.
- Agent/model calls and other nondeterministic operations are outside ordinary orchestrator code.
- Existing completed work is not regenerated solely because orchestration replays.

### Phase 6: Assignment, notifications, and human contributions

**Implement**

- Random assignment within the configured eligible/available pool, excluding duplicates and validator overlap.
- Persist selection once; randomness occurs outside replayed orchestrator logic.
- Required individual approaches, shared discussion, synthesized guidance, and unresolved concerns.
- Notification adapter, durable reminders/deadlines, reassignment, and escalation.
- Authenticated API responses correlated to input request, person, and revision.

**Verify**

- Missing contributions block Collaboration.
- Duplicate/early event delivery does not lose or double-apply a response.
- An insufficient pool produces escalation, not relaxed requirements.
- Restart during a multi-day wait preserves assignments and accepted responses.
- Disagreement is visible and cannot be silently converted into agreement.

### Phase 7: Planning, approval, and invalidation

**Implement**

- Approved implementation-plan template and criterion-to-step/test mapping.
- Plan approval, rejection, revision requests, and policy-based authorization.
- Approval binds to the exact plan and relevant context revisions.
- Explicit artifact staleness and reevaluation after material upstream changes.
- Versioned orchestration strategy so deployments do not break in-flight workflows.

**Verify**

- Execution cannot start with a stale or missing required approval.
- Disabling plan approval leaves other human duties active.
- Source drift triggers compatibility review under the configured policy.
- Pause/resume and cancellation are tested at stage boundaries.

**First usable milestone:** Phases 1-7 produce a complete API-only text-to-approved-plan workflow.

### Phase 8: Isolated implementation and pull-request publication

**Implement**

- Dedicated jobs with source revision, plan revision, scoped tools, network policy, and resource/time budgets.
- Developer agent edits, tests, documentation, deviation reporting, and progress output.
- Separate publication credentials; no direct writes to protected branches.
- Job lease/status/result handling, cancellation, bounded repair attempts, and workspace cleanup.
- Idempotent branch/pull-request publication and recovery of uncertain outcomes.

**Verify**

- One approved pilot change produces a real reviewable pull request.
- Repository commands cannot access host secrets or unrelated workspaces.
- Material deviations return to planning rather than expanding scope.
- Workflow cancellation signals the runner and records whether the job actually stopped.
- A worker crash cannot publish the same change twice.

### Phase 9: Independent validation and integrated candidate checks

**Implement**

- Existing CI integration plus applicable build, lint, unit, integration, and acceptance checks.
- Evidence mapping for every acceptance criterion and independent validator-agent review.
- Required human validator decision against the exact candidate commit.
- Defect routing to Execution, Planning, or Collaboration.
- Revalidation after integration changes, including cross-repository compatibility.

**Verify**

- Generated tests alone are not sufficient to pass a criterion.
- Failed/missing checks or human rejection prevent release.
- Approval of one commit does not authorize a different candidate.
- No cross-repository release proceeds without compatible integrated evidence.

**Pilot completion milestone:** Phases 1-9 deliver text-to-reviewed-pull-request with real CI evidence.

### Phase 10: Deployment, observation, and controlled autonomy

**Implement**

- Release candidates referencing immutable artifacts and exact included work items.
- Approved staging deployment, smoke/integration checks, and health observation.
- Production approval, deployment adapter, environment isolation, and recovery procedures.
- Explicit migration containment where database changes cannot simply be reversed.
- Feedback intake, verified knowledge refresh, and proposed skill improvements.
- Evaluation-driven autonomy policies with separate authorization for changes to human duties.

**Verify**

- Staging and production promote the same verified artifact.
- Failed health checks initiate tested recovery and corrective work.
- Retry/restart does not duplicate a deployment.
- Skills cannot self-activate from unverified feedback.
- No autonomy promotion occurs without agreed evidence and an approved policy revision.

Production deployment is a later milestone, not part of the first pull-request pilot.

## 8. Cross-cutting validation and operations

- Turn AC-01 through AC-17 into an acceptance matrix linked to automated or explicitly manual evidence.
- Use unit tests for policies, schemas, assignment constraints, and transitions.
- Use integration tests for database/outbox, authentication, storage, credentials, provider adapters, and Durable events.
- Inject failures at persistence/dispatch boundaries, during worker execution, and during human waits.
- Evaluate agents on representative pilot requests, ambiguity, stale evidence, malformed output, and prompt injection.
- Keep trusted acceptance expectations independent from developer-generated tests.
- Expose stage, current action, blockers, cost, duration, and correlation IDs through API events and operational telemetry.
- Runner/CLI operations show phase markers, per-item progress or frequent heartbeats, and final succeeded/failed/skipped totals.
- Redact sensitive data; disable request-body capture for credential operations and sensitive agent payloads.
- Bound tokens, tool calls, job duration, disk usage, and retries; budget exhaustion becomes an explicit blocked/failed state.
- For long-lived orchestrations, bound history growth and preserve revision-aware waits when rotating history.
- Build/test each increment before proceeding; add cloud/real-provider checks only when approved dependencies and access are available.
- Use Aspire-backed local integration tests for resource startup, API-to-Functions communication, PostgreSQL migrations, and workflow recovery; ensure test resources are isolated from persistent developer data.

## 9. What I can implement and what requires your access

I can create the C# solution, Aspire AppHost and F5 setup, PostgreSQL persistence/migrations, API, configuration schemas, agent/skill definitions, Durable workflows, repository adapters, runner, tests, local setup, and deployment definitions in this workspace.

I can validate locally and run authorized live integrations when the necessary tools and credentials are available.

You must provide or approve:

- Pilot repository URLs and read credentials through the secure credential path.
- Model provider/deployment and any required preview-package approval.
- Developer identities, roles, notification integration, and assignment policy.
- Azure subscription/permissions if cloud provisioning or deployment is desired.
- Write credentials, CI access, and deployment authority for later milestones.

Tests using doubles will be labeled as such. Real model behavior, private repository access, notifications, CI, and deployment will not be reported as verified unless actually exercised.

## 10. Definition of done

For the pull-request pilot:

1. Configuration registers a product, repositories, people, policies, and versioned agents/skills.
2. Private repositories are securely read and a reviewed evidence-backed baseline exists.
3. An authenticated caller submits feature text and receives an asynchronous run reference.
4. Agents generate contextual features, work items, and an implementation plan.
5. Required engineers contribute and approve through REST endpoints.
6. An isolated job implements the authorized plan and publishes a reviewable pull request.
7. CI and criterion-level evidence are recorded; the assigned engineer validates the exact candidate.
8. Restarts, duplicate requests, stale approvals, missing inputs, and failures are handled explicitly.
9. Every client-facing action works without a UI.
10. Documentation includes setup, configuration, API usage, credentials, testing, and operational recovery.
11. A single F5 starts the local application and its infrastructure after one-time setup, with API/Functions debugging and Aspire resource health/logs available.
12. Business data persists in PostgreSQL across restarts, and database migrations plus Durable recovery are verified independently.

For production completion, add a verified staging/production release path and exercised recovery from Phase 10.

## 11. Recommended next action

Approve this plan, resolve the Phase 1 decisions, and implement the compatibility spike before building the full workflow. Complete and verify each phase in order; do not start production automation before the pull-request pilot passes.

## References

- [Microsoft Agent Framework Durable Extension](https://learn.microsoft.com/agent-framework/hosting/azure-functions)
- [Durable Functions .NET isolated worker](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-dotnet-isolated-overview)
- [Durable Functions orchestrator constraints](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-code-constraints)
- [Durable Functions external events](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-external-events)
- [Application secret protection](https://learn.microsoft.com/azure/well-architected/security/application-secrets)
- [Aspire documentation](https://aspire.dev/)
