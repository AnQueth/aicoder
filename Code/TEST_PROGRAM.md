# What the test programs do

The repository has local automated tests, a live Copilot-category test, and a
local API smoke script.

## Automated tests

Run them with:

```powershell
dotnet test tests\Collaborator.Tests\Collaborator.Tests.csproj --filter 'TestCategory!=LiveCopilot'
```

The filtered tests run locally without starting PostgreSQL or the app. They check
things such as input validation, skill and artifact rules, workflow decisions,
and persistence behavior using test data.

Repository tests cover remote URL and branch-name validation, CLI flags and
environment overrides, and repository-update JSON. Git integration tests download
temporary repositories, select a non-default branch even when a tag has the same
name, reject missing branches, advance the base after pinning, and verify that the
published review commit's parent remains the pinned commit. They exercise HTTPS
and SSH URL handling using process-scoped Git URL rewriting to local fixtures;
they do not contact a hosted service or test real network authentication.

### Live Copilot test

`CopilotUsesRepositoryToolsToGroundAndRevisePlan` is tagged `LiveCopilot`.
It authenticates with the host's Copilot credentials, calls a real model, and
consumes usage while checking repository-tool grounding and plan revision.
Its `COLLABORATOR_TEST_COPILOT` environment guard is currently commented out;
an unfiltered run includes it. Do not rely on leaving that variable unset to
prevent model calls. Run it deliberately with:

```powershell
dotnet test tests\Collaborator.Tests\Collaborator.Tests.csproj --filter 'TestCategory=LiveCopilot'
```

## Local API smoke test

Run `scripts\Verify-Local.ps1` against a running local AppHost. It sends real
HTTP requests to the app and writes test products, skills, reviewers, feature
runs, decisions, and audit events to the development database. It does not
remove those records.

The script checks:

- The API is ready and rejects a request without an API key.
- Business analyst, architect, and developer skills, reviewers, and a synthetic
  product can be configured. The script reuses its stable `developer-smoke` skill
  and disables temporary smoke skills created by earlier script versions.
- The architecture skill is available, and the generated architecture analysis
  can be reviewed and approved.
- Invalid feature input is rejected.
- Duplicate feature submissions reuse the same run, while changed input with
  the same idempotency key is rejected.
- The business analyst specification can be edited, revised, approved, or
  rejected, and invalid or contradictory decisions are rejected.
- Every captured developer skill has a matching analysis and revision reference,
  and cross-role synthesis is available after architecture approval.
- Plan review assignments and responses are enforced, including blocking
  coordinator approval until all required reviews are received.
- A rejected plan does not start code generation, and expected events appear in
  the audit history.

### Architecture approval coverage

The script approves the architecture analysis before continuing to plan review.
It checks that architecture history exists, invalid and unknown revision
decisions are rejected, and the approved architecture is included in the audit
history.

The script requires an HTTPS, SSH, or local `file://` Git repository URL in
`COLLABORATOR_PRODUCT_REPOSITORY_URL`. Select a base branch with
`-RepositoryBaseBranch` or `COLLABORATOR_PRODUCT_BASE_BRANCH`; otherwise the
repository default is used. Remote access needs Git credentials configured on
the workflow host. It reads the API key from
`COLLABORATOR_API_KEY` or the AppHost user secrets and accepts `-BaseUrl` to
target a different local API. Waiting time scales with the number of captured
developer skills; `-TimeoutSeconds` sets the minimum wait allowance (900 seconds
by default).

### Running against empty data volumes

The smoke test does not require existing products, reviewers, or skill
configuration. On a new database, the AppHost migration creates the schema and
the script configures the BA, architecture, and developer skills, creates its
reviewers and product, then exercises the workflow. This was verified on
2026-10-13 with empty PostgreSQL and Azurite volumes: all 53 checks passed.

To repeat that first-run check, stop the AppHost and remove only the two local
development volumes before starting it again:

```powershell
dotnet tool run aspire -- stop --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
docker volume rm collaborator-postgres-data-v2 collaborator-azurite-data
dotnet tool run aspire -- start --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
dotnet tool run aspire -- wait api --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

Removing these volumes permanently deletes local products, reviews, approval
waits, and Durable Functions state. It is only needed to repeat a blank-volume
check; normal stop/start preserves data.

During a wait, the script prints and records the first run-status check only.
Further queue polls are omitted individually and summarized at the end with the
final status, number of checks, and elapsed time. This keeps the report readable
while retaining evidence of the initial state and final result.

Each run writes a Markdown transcript to
`test-results/Verify-Local-<timestamp>.md` under the `Code` directory. It records
the HTTP method, URL, request body, response status, and response body for each
exchange, including generated specifications, architecture, specialist analyses,
collaboration synthesis, plans, and audit events. It also records safe request
headers and named HTTP status checks. Authentication and other secret-like
headers are redacted. Pass `-ReportPath` to choose another output file. The
transcript does not include the internal model prompts or tool messages used
inside the workflow; it records the test program's HTTP inputs and the
application's HTTP outputs. The generated directory is ignored by Git because
reports may contain product or repository details.
