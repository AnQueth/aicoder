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

### Per-run specialist selection verification

On 2026-10-15, 34 focused tests passed covering selection, prompt/revision
preservation, CLI and request payloads, planner context, and Git review-branch
publication, including an empty specialist set. After adding an explicit
no-synthesis-model assertion, all 17 selection tests passed again.
The empty-set case also verifies that human plan/reviewer approval is still
required. The API built without warnings or errors, the EF model matched its
migration, and the smoke script passed PowerShell syntax validation.
The generated Functions extension build reported an NU1900 warning because
package vulnerability metadata access was denied.

These are local tests, not a live Copilot rerun. The additive
`PerRunSpecialistSelection` migration was generated but not applied to the live
database during this verification.

### Live six-number calculator feature

On 2026-10-15, the updated Aspire stack applied the migration and the real
scripted API client submitted a fresh calculator request with `--no-specialists`.
Run `36179833-2e7a-4100-8ee4-01286755b874` reached `code_change_ready` in 601.6
seconds, with 1,190 HTTP requests and zero non-2xx responses. BA, architecture,
reviewer, and plan approvals were submitted automatically for the demonstration;
they were not independent human sign-off.

The API confirmed `developerSkillCount: 0` and `developerSkills: []`.
The generated review branch was
`collaborator/feature-361798332e7a41008ee401286755b874`, commit
`697b41c313eecbd3f17b4af90dc30b7f9208cd90`. Its parent and unchanged `main`
were both `da420a5846bbca95e4be5b7d9eb7a6f00387536d`; nothing was merged.

Independent verification of that exact published commit:

- `node --test`: 10 passed, zero failed.
- `dotnet build --disable-build-servers --verbosity minimal`: zero warnings/errors.
- Edge browser checks: six positive integers produced 21; mixed signs produced
  0; decimals produced 18; six zeros produced 0; blank input displayed an error.
- Existing task add, completion, filtering, local-storage reload, and deletion
  passed browser regression checks. No browser JavaScript errors were observed.

The generated changes include the reusable calculator, six-input accessible UI,
automated tests, and a manual QA plan in the sample repository's README.

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
  product can be configured through the shared agent-skill API. The script reuses
  and explicitly selects its stable `developer-smoke` fixture skill, without
  modifying other user-defined specialist prompts.
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

## Automatic client verification

On 2026-10-15, the live `--auto` client completed the Taskboard clear-completed
feature flow using Copilot, the local Aspire stack, one configured reviewer, and
an explicit scripted approval response. Run
`3d18e692-1fa7-41d6-8ee1-b5db07d7c364` reached `code_change_ready` with client exit
code 0 in 550.9 seconds. The sample remote retained review branch
`collaborator/feature-3d18e6921fa741d68ee1b5db07d7c364` at commit
`5fd233d651b74897b492cf2c73d4596fd31409a1`. Its parent matched the pinned base
`da420a5846bbca95e4be5b7d9eb7a6f00387536d`, and `main` was unchanged.
The verification stack was stopped afterward.

The associated focused regression run passed 34 tests, including binary-byte
preservation through persisted patches and Git publication, configuration and
dotfile changes, and validation beyond the removed file-count/content caps.
These checks verify the client/workflow and file policy, not independent QA or
UAT approval of the generated feature. Scripted approvals are development-only
test inputs.
