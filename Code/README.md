# Collaborator foundation

A headless .NET 10 foundation for the [implementation plan](../IMPLEMENTATION_PLAN.md).
It implements text intake, PostgreSQL persistence, a BA specification review gate,
developer review and plan approval, Copilot-backed repository analysis and patch
generation, and publication of a review branch to a configured local Git repository.
It is not yet the complete repository-aware development platform: repository URLs
are limited to local `file://` sources, generated changes are not built or tested,
and no hosted pull request, merge, or deployment is created.

## Local prerequisites

- .NET SDK 10.0.401 (pinned in `global.json`).
- Docker-compatible container runtime running.
- Azure Functions Core Tools v4 with .NET 10 isolated-worker support.
- VS Code C# Dev Kit and Aspire extensions for F5.
- Local HTTPS development certificate trusted (`dotnet dev-certs https --trust`).
- PowerShell 7 for the smoke test.

From this directory:

```powershell
dotnet tool restore
dotnet build Collaborator.slnx
dotnet test tests\Collaborator.Tests\Collaborator.Tests.csproj
```

Both Aspire CLI 13.6.0 and EF tooling are pinned in `dotnet-tools.json`. Use the local
CLI commands below rather than an older globally installed Aspire CLI.

## One-time local secrets

The AppHost requires two different development keys, each at least 32 characters.
Generate random keys and send them to user secrets without putting their values in
source files or command arguments:

```powershell
$secrets = @{
    'Parameters:api-key' = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    'Parameters:workflow-key' = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}
$secrets | ConvertTo-Json | dotnet user-secrets set --project src\Collaborator.AppHost
```

Do not repeat this command unless intentionally rotating the keys. User secrets are
local development storage, **not an encrypted production vault**. Repository
checkout access is not required; Copilot authentication is configured separately below.

## F5 and local resources

Open the **repository root (`aicoder`)** as the VS Code workspace — the launch
configurations live in [../.vscode/launch.json](../.vscode/launch.json). Install the
recommended extensions, make sure Docker is running and the one-time secrets are set,
then in **Run and Debug** pick one of:

- `Collaborator: Aspire extension (debug all services)` — Aspire extension; breakpoints
  work in the API and Workflows projects.
- `Collaborator: Aspire (.NET 10)` — C# Dev Kit; debugs the AppHost process.

Press F5; the Aspire dashboard opens in the browser. The first run pulls container
images (PostgreSQL, Azurite, Key Vault emulator) and may prompt to trust the Key
Vault emulator's self-signed certificate. Alternatively:

```powershell
dotnet tool run aspire -- start --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
dotnet tool run aspire -- wait api --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
dotnet tool run aspire -- describe --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

The AppHost launches:

| Resource | Purpose |
|---|---|
| PostgreSQL 17 | Developer configurations, review assignments and responses, product briefs and repository URLs, feature runs, plans, decisions, audit events, outbox |
| Migration executable | Applies EF migrations before API and workflows start |
| Azurite | Durable Functions history, control queues, and leases |
| Key Vault emulator | Serves `Security--ApiKey` / `Security--WorkflowKey` secrets to API and workflows |
| Functions isolated worker | Durable workflow, Copilot SDK planning activities, internal HTTP bridge |
| REST API | User-facing intake, polling, events, and decisions |
| Aspire dashboard | Local resource status, logs, and telemetry |

The EF migration history is a single fresh `InitialCreate` baseline for the current
model. It intentionally does not upgrade databases created by the earlier migration
chain. The AppHost uses a new named PostgreSQL volume for this baseline and leaves
the earlier development volume untouched. Existing data from that volume is not
visible to this version.

PostgreSQL and Azurite use named persistent volumes. Normal stop/start preserves
approval waits. Do not delete those volumes to restart. Fixed local project ports
mean multiple copies should not run simultaneously. The local API defaults to
`http://localhost:5076` (HTTPS also available); use Aspire's reported endpoints if
launch settings change.

Secrets are served by the
[Azure Key Vault emulator](https://github.com/james-gould/azure-keyvault-emulator)
via its `AzureKeyVaultEmulator.Aspire.Hosting` package
(`AddAzureKeyVault("keyvault").RunAsEmulator()`). The AppHost seeds the vault
from its `api-key` / `workflow-key` parameters (user secrets). API and workflows
receive the vault URI as `ConnectionStrings:keyvault` plus emulator credentials,
and `AddKeyVaultSecrets()` (ServiceDefaults) loads them into configuration
(`Security--ApiKey` becomes `Security:ApiKey`). When no `keyvault` connection
string is present the services fall back to normal configuration. The emulator
requires Docker and a trusted local SSL certificate; the emulator creates and
installs it on first run (accept the trust prompt). The Azure Storage Durable backend is selected,
so a Durable Task Scheduler emulator is not required.

## Explicit model profiles

**Copilot is the configured local AppHost mode** in
`src\Collaborator.AppHost\appsettings.Development.json`. `Agents:Mode` selects
which planner runs; it is ordinary configuration, not a secret. Planning uses `GitHub.Copilot.SDK`
1.0.16, which downloads and verifies its pinned CLI runtime during build. No
Azure OpenAI endpoint, deployment, or model-provider key is required.

Sign in to GitHub Copilot CLI as the same OS user running the AppHost. The SDK
uses the stored CLI credentials by default. Your account must have Copilot
access allowed by organization policy; requests consume applicable Copilot usage.
For automation, use a supported token through `COPILOT_GITHUB_TOKEN` in the
worker environment, never in tracked files or prompt text. See
[SDK authentication](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate).
No user-secret entry is needed for Copilot. The planner mode is normal
configuration (`Agents:Mode` / `Agents__Mode`); the model can be set with
`Agents:Copilot:Model` / `Agents__Copilot__Model`. If unset, the Copilot runtime
selects its default model.

Restart the AppHost after configuration changes. `AzureOpenAI` mode is no
longer supported.

Each activity creates a fresh Copilot session. Before the BA draft, the workflow
shallow-clones the configured local Git source without checking files out, reads
bounded tracked source and documentation, and pins the commit to the run. When a
snapshot is available, BA, cataloging, planning, and implementation sessions receive
read-only tools to list eligible paths, search source lines, and read an exact file.
The tools are bound to the run's pinned, in-memory snapshot; raw source is removed
from the prompt payload, and no arbitrary filesystem, shell, or write tools are
available. Catalogs are cached per product commit and pinned to each feature run;
plan revisions reuse that same commit-pinned catalog and source. A changed commit
is indexed for a new run. Planning revisions include the prior plan and feedback.
Configuration discovery and embedding retrieval are disabled; only registered
read-only repository tools can execute. Each Copilot call has a three-minute
deadline, requests an explicit JSON schema, and validates the result
against the plan contract. Bounds are described in the schema and planner definition
and enforced by application validation because model providers support only a subset
of JSON Schema constraints. The SDK's structured-output and permission-decision APIs
are experimental in this pinned version; their diagnostics are acknowledged only at
the call sites.
Authentication, runtime, and invalid-output errors fail the workflow;
there is no automatic live-to-offline fallback. Sessions and runtime processes
are disposed after each call. The SDK can retain session transcripts locally;
treat its local storage as sensitive and do not submit credentials.

For deterministic planning tests without Copilot access, set `Agents:Mode` to
`Offline`. Offline results explicitly state that no model or repository analysis
was performed; repository catalog generation is Copilot-only and never silently
falls back to an ungrounded plan.

An opt-in live SDK test generates and revises a synthetic plan using a hidden
repository snapshot. It verifies the plan cites a source-only method and that
Copilot actually invokes all three repository tools (list, search, and read).
The test consumes Copilot usage and requires authentication; regular tests skip it:

```powershell
$env:COLLABORATOR_TEST_COPILOT = '1'
dotnet test tests\Collaborator.Tests --filter 'TestCategory=LiveCopilot'
Remove-Item Env:\COLLABORATOR_TEST_COPILOT
```

The versioned [business analyst](src/Collaborator.Agents/Definitions/business-analyst.md),
[cataloger](src/Collaborator.Agents/Definitions/cataloger.md),
[planner](src/Collaborator.Agents/Definitions/planner.md),
[reviewer selector](src/Collaborator.Agents/Definitions/reviewer-selector.md), and
[implementer](src/Collaborator.Agents/Definitions/implementer.md) definitions are
copied to the worker output. The BA's configurable skill prompt is stored and
versioned separately from the role definition. Discovery, architect, and validation
agent roles remain unimplemented.

## Sample Git-source target

A separate sample product repository is available at
[`../../collaborator-sample-app`](../../collaborator-sample-app/README.md). It is a small
Taskboard website. A local bare Git repository at
`H:\src\collaborator-sample-app-remote.git` is configured as its `origin`; it is a
simulated remote stored on this machine and is not hosted or published. The
`Collaborator: API test client (interactive)` F5 configuration registers the
`taskboard-sample` product with this `file://` URL. Product repository URLs are
persisted in PostgreSQL and can be set with `--repository-url` or
`COLLABORATOR_PRODUCT_REPOSITORY_URL`.

Repository inspection currently supports local `file://` Git sources only; HTTPS
and SSH sources are rejected until authenticated, safe remote fetching is designed.
Only tracked text files with supported source/documentation extensions are sent for
analysis. Hidden directories, `.env*`, secret-named files, `appsettings*`, lock
files, generated/vendor directories, and binary files are excluded. The indexer
rejects repositories with more than 64 eligible files, files over 12,000 characters, or more than 160,000
total source characters. It analyzes the repository's default `HEAD` and records
the exact commit. Source text is sent to the signed-in GitHub Copilot SDK runtime;
do not use repositories whose contents you are not permitted to send to Copilot.
The sample is intentionally not part of `Collaborator.slnx` or AppHost, so
repository tools can operate on it as an independent Git target. From the
`aicoder` VS Code workspace, use **Sample Product: Taskboard (F5)** to build and
debug it, or follow that repository's README to run it directly.

## REST contract

Requests under `/api/v1` and the OpenAPI document require `X-Api-Key`.
The Functions internal bridge uses a separate `X-Workflow-Key`; clients must not
call it directly. `/health` and `/alive` are local readiness/liveness endpoints.

| Method | Route | Input / behavior |
|---|---|---|
| POST | `/api/v1/products` | `{ "id": "billing", "name": "Billing", "brief": "Reviewed product context", "repositoryUrl": "file:///H:/src/billing.git" }`; URL optional; 201 |
| PUT | `/api/v1/products/{id}/repository` | `{ "repositoryUrl": "file:///H:/src/example.git" }`; set or clear the source URL |
| GET | `/api/v1/products/{id}` | Product brief and optional repository URL; 200 or 404 |
| GET | `/api/v1/products/{id}/repository-catalog` | Reusable catalog, source commit, and generation time; null catalog until indexed |
| GET | `/api/v1/developers` | List configured reviewer profiles |
| PUT | `/api/v1/developers/{userId}` | Create or update reviewer display name, roles, review focus, and enabled state |
| GET | `/api/v1/agent-skills?roleId=business-analyst&domain=general` | List each skill's active revision and enabled state |
| GET | `/api/v1/agent-skills?roleIdPrefix=developer-` | List all active developer-role skills |
| GET | `/api/v1/agent-skills/{roleId}/{domain}/{skillId}/revisions` | List immutable skill revisions, newest first |
| POST | `/api/v1/agent-skills/{roleId}/{domain}/{skillId}/revisions` | Append a revision with `{ "name": "...", "prompt": "...", "isEnabled": true, "changeReason": "...", "actorId": "..." }`; advances the active revision |
| GET | `/api/v1/business-analysts/{analystId}/skills` | Legacy alias listing shared business-analyst skills in the `general` domain |
| PUT | `/api/v1/business-analysts/{analystId}/skills/{skillId}` | Legacy alias that appends a revision to the shared `business-analyst/general` skill |
| POST | `/api/v1/products/{id}/feature-requests` | `{ "text": "...", "businessAnalystId": "default", "businessAnalystSkillId": "requirements-analysis" }` plus `Idempotency-Key`; 202 with run ID and status URL |
| GET | `/api/v1/runs/{id}` | Status, BA specification/revision/decision, architecture revision, captured developer-skill count, plan/revision/decision, failure code, and review-branch metadata |
| GET | `/api/v1/runs/{id}/feature-specifications` | Every formal specification revision, decision, feedback, and question-answer set |
| PUT | `/api/v1/runs/{id}/feature-specifications/{revision}` | Replace the current unapproved specification revision |
| POST | `/api/v1/runs/{id}/business-analyst-decisions` | `{ "revision": 1, "decision": "revise", "comment": "Additional context", "answers": [{ "question": "Which invoices?", "answer": "Only selected invoices." }] }`; revise must answer every open question |
| GET | `/api/v1/runs/{id}/architecture` | Architecture analysis revisions, evidence, upstream revision references, skill revisions, pinned commit, and review decisions |
| POST | `/api/v1/runs/{id}/architecture-decisions` | `{ "revision": 1, "decision": "approve" \| "revise" \| "reject", "comment": "...", "actorId": "..." }`; 202 |
| GET | `/api/v1/runs/{id}/specialist-analyses` | The analysis from every captured developer skill, including its skill revision and repository commit |
| GET | `/api/v1/runs/{id}/collaboration` | Cross-role agreements, conflicts, and unresolved questions with exact source skill revisions |
| GET | `/api/v1/runs/{id}/events?after=0` | Up to 100 audit events after the event cursor |
| GET | `/api/v1/runs/{id}/plans` | Every plan revision with its decision and feedback |
| GET | `/api/v1/runs/{id}/assignments` | Reviewer assignments and their revision-specific decisions/responses |
| POST | `/api/v1/runs/{id}/assignments/{userId}/response` | Submit a selected reviewer's approve/revise response for the current plan revision |
| POST | `/api/v1/runs/{id}/plan-decisions` | `{ "revision": 1, "decision": "approve" \| "reject" \| "revise", "comment": "..." }`; 202 |
| GET | `/openapi/v1.json` | Machine-readable REST description |

Skill revisions are append-only. The active pointer and enabled state are updated
when a revision is added; disabling a skill also creates a new revision. The legacy
BA routes address the shared `business-analyst/general` skills, not analyst-specific
copies. New databases start with an empty skill registry; configure a business
analyst skill through the generic agent-skill API before submitting runs. Existing
databases are not upgraded from the earlier schema. Supplied `actorId` values are
audit labels only and are not authenticated identities.

Developer skills use role IDs beginning with `developer-`, for example
`developer-ui`, `developer-backend`, or `developer-security`. Create each role
through the generic append-only skill endpoint, using any valid domain and skill
IDs:

```powershell
$headers = @{ 'X-Api-Key' = $apiKey }
$skill = @{
  name = 'UI developer analysis'
  prompt = 'Review the feature from the UI role. Identify screens, interaction, accessibility, and UI test changes.'
  isEnabled = $true
  changeReason = 'Initial role skill'
}
Invoke-RestMethod -Method Post `
  -Uri "$baseUrl/api/v1/agent-skills/developer-ui/general/ui-analysis/revisions" `
  -Headers $headers -ContentType 'application/json' -Body ($skill | ConvertTo-Json)
```

All enabled `developer-*` skills are snapshotted, including their prompts and
revision numbers, when a feature run is submitted. After architecture approval,
canonical Durable workflow runs a separate bounded activity for each captured
skill and persists each result with its skill revision and repository commit. A
final aggregation step verifies that every captured role has exactly one result
before cross-role synthesis. Synthesis links agreements, conflicts, and
unresolved questions to the exact skill revisions; it is advisory and does not
replace or hide individual analyses. Planning receives both the complete
original analyses and synthesis. The API and console show the synthesis
alongside the individual results before plan approval. Review all outputs: the
workflow does not select only roles it thinks are relevant, and the synthesis
must not silently settle disagreements. Adding or editing a role later affects
new runs, not runs already submitted. Feature submission returns 409 if no
enabled developer skill is configured. The console client initializes a general
developer skill only when none are configured.

This is a new product and does not support replaying Durable histories created by
older workflow implementations. Before deploying a workflow change, ensure there
are no active instances that depend on the previous implementation.

Each new run snapshots the selected BA skill name, revision, and prompt, so later
skill edits do not alter work already in progress. The BA agent creates a structured
feature specification; the BA can replace the unapproved draft or request a revised
draft before approving it. To request an AI revision, the BA must answer every open
question on that exact specification revision. Answers and optional feedback are
preserved in revision history and supplied as context to the next BA draft. After
BA approval, the architect skill analyzes the approved specification against the
pinned repository snapshot. Each component impact must cite inspected repository
evidence; the saved artifact records its source commit, input revisions, and exact
architect skill revision. A human must approve this revision before developer
planning starts. Revision requests include the prior analysis and feedback;
rejection stops the run. Once architecture is approved, every captured developer
role skill runs against the same approved specification, architecture, and pinned
repository snapshot. The plan-generation stage receives all specialist results,
and the console client displays them before plan review. The implementation plan
has its own developer review and approval gate. Missing upstream approval or an
incomplete specialist result set cannot advance the run to code generation.

The shared development API key is not role authentication. Actor IDs are audit
labels only, not proof of identity; the API does not establish that the caller is
the named business analyst or developer. Use this workflow only in the development
environment until role-based authentication and authorization are implemented.

The API accepts feature text up to 16,000 characters, product IDs up to 80
lowercase letters/digits/hyphens, names up to 200 characters, and idempotency keys
up to 128 characters. Planning currently accepts only local `file://` Git
repositories. Feature submissions without a configured local source return 409.
Repository inspection or Copilot failures fail the run and never fall back to an
ungrounded recommendation. Run status includes its pinned catalog and commit, and
the console client prints the functionality entries and source locations. The
HTTP body limit is 32 KiB.

Business specifications, architecture assessments, and plans are validated before
they are saved. If one of these AI responses violates its output contract, the
Copilot session gets one correction request with the validator's field-specific
feedback. A second invalid response still fails the run; invalid artifacts are
never accepted as a fallback. See [ADR 0007](../ADR/0007-retry-invalid-ai-artifacts.md).

Same product/key/text returns the existing run. Reusing the key with different
text returns 409. Decisions require the generated revision and an approval wait;
repeated identical decisions are accepted, contradictory decisions return 409.
`reject` and `revise` require a comment (up to 4,000 characters). `revise` sends
the coordinator's comment, all completed developer responses for that plan
revision, the prior plan, and feature text back to the planner. Pending assignment
responses are excluded. The planner produces the next revision; at most 5 revisions
are allowed, after which only approve/reject are accepted (409 otherwise).
PostgreSQL concurrency tokens prevent racing decisions from overwriting one another.

Workflow progression:

```text
queued -> business_analyst_specification_generating
       -> awaiting_business_analyst_review
              | approve -> architecture_analysis_generating
                         -> awaiting_architecture_review
                                | approve -> developer_skills_processing
                                           -> planning -> awaiting_approval
                                | revise  -> architecture_analysis_generating
                                | reject -> architecture_rejected
              | revise  -> business_analyst_specification_generating
              | reject -> feature_request_rejected
                                      | approve -> plan_approved -> code_change_generating
                                      | revise  -> planning
                                      | reject -> plan_rejected
                                                                -> code_change_ready | failed
          repository_catalog_generated / repository_catalog_reused
          dispatch exhausted -> blocked
```

A transaction persists submission/decision, audit, and outbox intent together.
The dispatcher uses stable Durable instance IDs, serializes deliveries across
API hosts, and retries up to ten times with backoff. It owns HTTP retry scheduling
rather than stacking transient HTTP retries. Approval generates a bounded patch
against the run-pinned repository commit and publishes it to the unique
`collaborator/feature-{runId}` review branch. Run status reports the branch name,
commit, changed paths, and patch summary; patch contents are not exposed by the
API. The branch is prepared through Git's index and object database, so generated
files are never checked out or executed. The source/default branch is not changed,
and no merge or deployment is performed automatically. Rejecting a plan does not
invoke code generation.

## API client console (`collab-client`)

`src\Collaborator.ApiClient` drives the flow end to end: BA, architecture, and
developer skill
setup, product registration, feature submission, BA specification edit/revise/
approve/reject, architecture review (approve / reject / request a revision),
per-developer review assignments and responses, developer plan review (approve /
reject / request a revision), final status, and artifact history.
Configure developer profiles with one or more engineering roles before plan
generation. The reviewer selector chooses exactly `--reviewer-count` enabled
developers (default three) whose roles fit the feature, and each receives a
separate prompt for every generated revision. Every selected developer must
submit an approve/revise decision on that exact revision before the coordinator
can decide. A revise response automatically requests an AI plan revision using
the developers' feedback; approval is allowed only after all selected reviewers
approve the same revision. Reviewer assignments and their role snapshots are
retained if profile settings later change.

### Run the client with F5

With the repository root (`aicoder`) open in VS Code:

1. Start `Collaborator: Aspire extension (debug all services)` with F5 and wait
   until the API and Workflows resources are healthy in the dashboard.
2. Leave that debug session running. In **Run and Debug**, select
   `Collaborator: API test client (interactive)` and press F5 to start a second
   debug session.
3. Enter your feature request in the client's **integrated terminal**. Review and
   optionally edit the BA specification, then approve, reject, or request a BA
   revision. After BA approval, review the repository-grounded architecture
   analysis and approve, reject, or request a revision. Developer assignments
   and the implementation plan follow; approve, reject, or request a plan revision.

The client F5 configuration builds the client and launches its DLL with the C#
debugger in the integrated terminal; it does not start Aspire itself or depend
on a C# Dev Kit active configuration identifier. It uses the existing API URL
and key defaults described below. If the API endpoint differs, set
`COLLABORATOR_API_URL` in the client configuration's `env` object in
[../.vscode/launch.json](../.vscode/launch.json).
Do not put API keys in tracked configuration files; use AppHost user secrets.

```powershell
# Interactive: configure three role-matched reviewers; answer each review and then decide on the plan
dotnet run --project src\Collaborator.ApiClient -- --reviewer-count 3 `
  --developer "alice|Alice|architecture|System design and repository fit" `
  --developer "bob|Bob|security|Authorization and data handling" `
  --developer "cara|Cara|testing|Acceptance and regression coverage" `
  --feature "Add invoice export"

# Update the default BA skill prompt; future submissions snapshot this revision
dotnet run --project src\Collaborator.ApiClient -- --business-analyst "ba-team" `
  --ba-skill "requirements-analysis" --ba-skill-prompt "Clarify user outcomes and acceptance criteria..." `
  --feature "Add invoice export"

# Scripted: all reviewers approve each plan revision; revise once, then approve
dotnet run --project src\Collaborator.ApiClient -- --feature "Add invoice export" `
  --reviewer-count 3 `
  --developer "alice|Alice|architecture|System design and repository fit" `
  --developer "bob|Bob|security|Authorization and data handling" `
  --developer "cara|Cara|testing|Acceptance and regression coverage" `
  --review-response "alice=approve" --review-response "bob=approve" --review-response "cara=approve" `
  --feedback "Include CSV and PDF formats and add a large-export test" --approve

# Non-interactive approve of the first plan; or resume an existing run
dotnet run --project src\Collaborator.ApiClient -- --auto
dotnet run --project src\Collaborator.ApiClient -- --run <run-guid>
```

Interactive mode is the intended human-review path. Scripted/non-interactive mode
automatically approves the BA and architecture drafts for development smoke tests;
those actions are not authenticated human decisions and must not be used as evidence
of sign-off.

The API key resolves from `--api-key`, then `COLLABORATOR_API_KEY`, then the
AppHost user secrets. `COLLABORATOR_API_URL` or `--base-url` overrides
`http://localhost:5076`. Other options: `--product`, `--reject <reason>`, `--timeout <s>`,
`--poll <ms>`, `--interactive`, `--quiet`, `--verbose`; run with `--help` for details. Exit codes:
0 approved/rejected, 1 failure, 2 invalid arguments, 130 cancelled.

## Verification

See [TEST_PROGRAM.md](TEST_PROGRAM.md) for a plain-language description of the
automated tests and local API smoke test.

With the local AppHost healthy:

```powershell
$env:COLLABORATOR_PRODUCT_REPOSITORY_URL = 'file:///H:/src/collaborator-sample-app-remote.git'
.\scripts\Verify-Local.ps1
```

The script reads the AppHost API key locally without printing it. It creates
three uniquely named reviewer profiles, collects a revise/approve review round
and an all-approve round, confirms that an early coordinator approval is blocked,
then rejects the final plan so the smoke test does not generate code. Override with
`COLLABORATOR_API_KEY`, `COLLABORATOR_PRODUCT_REPOSITORY_URL`, and `-BaseUrl` if
needed. It prints per-check progress and summarizes queue polling after the
first status check for each run. It leaves
synthetic products/runs/audit evidence in the development database; it does not
delete data. The checks cover BA skill setup, specification editing/revision/
approval, role-matched reviewer assignments, required reviewer responses,
unanimous same-revision approval gating, plan revision/rejection, BA rejection,
audit history, and confirmation that rejection does not start code generation.
Each run also saves request and response bodies to a Markdown transcript under
`test-results/` (ignored by Git); use `-ReportPath` to choose a different path.
The API key is omitted, and internal model prompts/tool messages are not part of
the HTTP transcript. The test also works with empty PostgreSQL and Azurite
volumes: AppHost applies the initial schema, and the script configures the skills
and records it needs. See [TEST_PROGRAM.md](TEST_PROGRAM.md#running-against-empty-data-volumes)
for the explicit, destructive blank-volume verification steps.

Verified on 2026-10-13, including a clean-volume end-to-end run:

- Full solution build: zero warnings/errors.
- Unit tests: 69 passed, zero failed.
- Local API smoke test on empty PostgreSQL and Azurite volumes: 53 passed, zero failed.

The clean-volume run confirms local resource startup, initial migration, workflow
dispatch, and smoke-test setup without pre-existing application records. The VS
Code launch configuration is provided, but an interactive **F5 debugger session**
was not verified.

Stop local resources when finished:

```powershell
dotnet tool run aspire -- stop --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

## Boundaries and next increments

This is a development-only pilot. The API deliberately refuses non-Development
startup until real identity is implemented. A shared key is not user attribution,
product authorization, or a production authentication design. Protect local ports
from untrusted networks; the Aspire dashboard is development infrastructure.

The BA-first feature intake, editable/versioned role-and-domain skill registry,
architecture analysis/revision/approval gate, all-enabled developer-skill analysis,
reviewer assignments/responses, and separate developer plan gate are implemented.
The local HTTP/AppHost smoke test,
live Copilot test, and interactive F5 debugging have not been run for this increment.

Still to implement, in the order described by the plan:

1. Product configuration import/export, repository definitions, read-token
   references and secure credential resolution.
2. Additional specialist developer, synthesis, work-item, and validation skills,
   plus notifications and collaboration contributions beyond plan review.
3. Entra/product-role authorization, isolated build/test execution, hosted
   pull-request creation, CI, release policy, and cloud deployment.

Developer review assignments and response tracking are available in this increment.
The current planner uses repository-grounded evidence where a repository is
configured, but does not execute tests or claim deployability.
