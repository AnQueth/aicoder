# Collaborator foundation

A headless .NET 10 starting point for the [implementation plan](../IMPLEMENTATION_PLAN.md).
This increment proves text intake, PostgreSQL persistence, GitHub Copilot SDK
planning inside Durable Functions activities, and durable human approval through REST.
It is not yet the complete repository-aware development platform.

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

Sign in to GitHub Copilot CLI as the same OS user running the AppHost. No
`dotnet user-secrets` command is needed for Copilot settings. To change the
planner mode, edit `Agents:Mode` in `appsettings.Development.json` or set the
`Agents__Mode` environment variable. To select a particular Copilot model, use
the ordinary `Agents:Copilot:Model` configuration key (or
`Agents__Copilot__Model` environment variable); leaving it unset uses the
Copilot runtime's default model.

Restart the AppHost after configuration changes. `AzureOpenAI` mode is no
longer supported.

Each activity creates a fresh tool-free Copilot session. Before its first plan,
the workflow shallow-clones the configured local Git source without checking files
out, inventories bounded tracked source and documentation, and asks Copilot for a
reusable functionality catalog containing exact paths and symbols. Catalogs are
cached per product commit and pinned to each feature run; plan revisions reuse that
same commit-pinned catalog. A changed commit is indexed before a new run is
planned. Planning revisions include the prior plan and feedback. Configuration
discovery and embedding retrieval are disabled; permission requests are denied. Each Copilot call has a
three-minute deadline, requests an explicit JSON schema, and validates the result
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

An opt-in live SDK test generates and revises a synthetic plan (consumes Copilot
usage and requires authentication). Regular tests skip it:

```powershell
$env:COLLABORATOR_TEST_COPILOT = '1'
dotnet test tests\Collaborator.Tests --filter 'TestCategory=LiveCopilot'
Remove-Item Env:\COLLABORATOR_TEST_COPILOT
```

The versioned [planner definition](src/Collaborator.Agents/Definitions/planner.md)
is copied to the worker output. Additional agents/skills are not yet implemented.

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
| POST | `/api/v1/products/{id}/feature-requests` | `{ "text": "Add invoice export" }` plus `Idempotency-Key`; 202 with run ID and status URL |
| GET | `/api/v1/runs/{id}` | Status, draft plan, revision, decision, failure code, and review-branch metadata |
| GET | `/api/v1/runs/{id}/events?after=0` | Up to 100 audit events after the event cursor |
| GET | `/api/v1/runs/{id}/plans` | Every plan revision with its decision and feedback |
| POST | `/api/v1/runs/{id}/plan-decisions` | `{ "revision": 1, "decision": "approve" \| "reject" \| "revise", "comment": "..." }`; 202 |
| GET | `/openapi/v1.json` | Machine-readable REST description |

The API accepts feature text up to 16,000 characters, product IDs up to 80
lowercase letters/digits/hyphens, names up to 200 characters, and idempotency keys
up to 128 characters. Planning currently accepts only local `file://` Git
repositories. Feature submissions without a configured local source return 409.
Repository inspection or Copilot failures fail the run and never fall back to an
ungrounded recommendation. Run status includes its pinned catalog and commit, and
the console client prints the functionality entries and source locations. The
HTTP body limit is 32 KiB.

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
queued -> awaiting_approval -> plan_rejected
                         \-> plan_approved -> code_change_generating
                                             -> code_change_ready | failed
               ^        |
               |        v (revise + feedback)
               +-- revision_requested
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

`src\Collaborator.ApiClient` drives the whole flow end to end: developer
configuration, product registration, feature submission, per-developer review
assignments and responses, coordinator plan review (approve / reject / give
feedback for a new plan), final status including generated review-branch details,
and plan/assignment history plus audit events. Configure each developer before
plan generation; every enabled developer
gets a separate prompt for each generated revision. Responses are stored as review
evidence, included in a later revision when the coordinator requests one, and do
not replace the coordinator's plan-level decision.

### Run the client with F5

With the repository root (`aicoder`) open in VS Code:

1. Start `Collaborator: Aspire extension (debug all services)` with F5 and wait
   until the API and Workflows resources are healthy in the dashboard.
2. Leave that debug session running. In **Run and Debug**, select
   `Collaborator: API test client (interactive)` and press F5 to start a second
   debug session.
3. Enter your feature request in the client's **integrated terminal**. Review
   each developer assignment, optionally record reviewer responses, then choose
   approve, reject, or feedback to request a revised plan.

The client F5 configuration builds the client and launches its DLL with the C#
debugger in the integrated terminal; it does not start Aspire itself or depend
on a C# Dev Kit active configuration identifier. It uses the existing API URL
and key defaults described below. If the API endpoint differs, set
`COLLABORATOR_API_URL` in the client configuration's `env` object in
[../.vscode/launch.json](../.vscode/launch.json).
Do not put API keys in tracked configuration files; use AppHost user secrets.

```powershell
# Interactive: configure a developer, review assignments, then choose [a]pprove, [r]eject, or [f]eedback
dotnet run --project src\Collaborator.ApiClient -- --developer "alice|Alice|security and privacy" --feature "Add invoice export"

# Scripted: two rounds of feedback, then approve the third plan
dotnet run --project src\Collaborator.ApiClient -- --feature "Add invoice export" `
  --developer "alice|Alice|security and privacy" --review-response "alice=Check export authorization and data handling" `
  --feedback "Include CSV and PDF" --feedback "Add a large-export test" --approve

# Non-interactive approve of the first plan; or resume an existing run
dotnet run --project src\Collaborator.ApiClient -- --auto
dotnet run --project src\Collaborator.ApiClient -- --run <run-guid>
```

The API key resolves from `--api-key`, then `COLLABORATOR_API_KEY`, then the
AppHost user secrets. `COLLABORATOR_API_URL` or `--base-url` overrides
`http://localhost:5076`. Other options: `--product`, `--reject <reason>`, `--timeout <s>`,
`--poll <ms>`, `--interactive`, `--quiet`, `--verbose`; run with `--help` for details. Exit codes:
0 approved/rejected, 1 failure, 2 invalid arguments, 130 cancelled.

## Verification

With the local AppHost healthy:

```powershell
.\scripts\Verify-Local.ps1
```

The script reads the AppHost API key locally without printing it. Override with
`COLLABORATOR_API_KEY` and `-BaseUrl` if needed. It prints per-check progress and
polls every two seconds. It leaves synthetic products/runs/audit evidence in the
development database; it does not delete data.

Verified during implementation:

- Solution build: zero warnings/errors.
- Unit tests: input validation (including plan decisions/revise), offline
  planning output, configuration fail-fast.
- HTTP smoke checks against actual PostgreSQL, Azurite, API, and Functions:
  authentication, validation, intake, idempotency, revise-with-feedback,
  approval/rejection, and audit.
- Full AppHost stop/start while awaiting approval: persisted plan survived and
  the Durable workflow resumed to `plan_approved` through the REST decision.
- Local resources healthy and migration executable finished successfully.

CLI startup/recovery was verified. The VS Code launch configuration is provided,
but an interactive **F5 debugger session** was not verified.

Stop local resources when finished:

```powershell
dotnet tool run aspire -- stop --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

## Boundaries and next increments

This is a development-only pilot. The API deliberately refuses non-Development
startup until real identity is implemented. A shared key is not user attribution,
product authorization, or a production authentication design. Protect local ports
from untrusted networks; the Aspire dashboard is development infrastructure.

Still to implement, in the order described by the plan:

1. Product configuration import/export, repository definitions, read-token
   references and secure credential resolution.
2. Required review contributions, alerts and response tracking.
3. Discovery, architect, developer, and validation agent definitions and skills.
4. Entra/product-role authorization, isolated coding workers, reviewed PRs, CI,
   release policy, and cloud deployment.

Developer review assignments and response tracking are available in this increment.
The current planner uses repository-grounded evidence where a repository is
configured, but does not execute tests or claim deployability.
