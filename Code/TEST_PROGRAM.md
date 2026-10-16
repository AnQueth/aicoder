# What the test programs do

The repository has local automated tests, a live Copilot-category test, and a
local API smoke script, plus Vue client tests.

## Vue client and directory routes

From `Code`, after installing the declared frontend dependencies:

```powershell
npm --prefix src\Collaborator.Web ci
npm --prefix src\Collaborator.Web test
npm --prefix src\Collaborator.Web run build
dotnet test tests\Collaborator.Tests\Collaborator.Tests.csproj --filter 'FullyQualifiedName~DirectoryEndpointsTests'
```

The frontend tests exercise HTTP headers/errors, credential clearing, product and
repository forms, reviewer roles, immutable prompt writes, specialist-selection
semantics, idempotent retries, exact revision/actor decision payloads, BA question
answers, approval gating, event pagination, terminal polling, stale-data failures,
and escaped artifact rendering. `npm run build` also runs strict Vue/TypeScript
checking before generating API-hosted static assets.

Directory tests start isolated loopback HTTP hosts backed by the existing EF
in-memory test provider. They verify paging, order, totals, error responses,
empty pages, and omission of internal prompts/keys/catalogs. They do not use the
running application database or send requests to Copilot. These are contract
and UI tests, not a live full-workflow acceptance run.

On 2026-10-16, all 37 frontend tests and all 10 directory HTTP tests passed.
Vue type checking, the production frontend build, and API Release publishing
succeeded. An isolated published API served the HTML and JavaScript assets,
rejected unauthenticated data requests, returned 400 for invalid pagination,
included both listings in OpenAPI, and returned 404 for unknown API routes.
Browser checks with fixture responses verified navigation, specialist selection,
credential-free browser storage, disconnect, and no horizontal overflow at
390px and 1440px widths. The existing running stack was not restarted or changed
during these initial isolated checks.

The broader directory-plus-foundation test run passed 125 of 126 tests. The
existing `RepositorySourceTools_ReadsWholeFilesAndSearchesBeyondOldRepositoryLimits`
test failed with `Git returned a malformed source-search result` in repository
search code outside this change. These initial checks did not include a live
Copilot workflow or real PostgreSQL acceptance run; the subsequent interactive
run is recorded below.

During the interactive UI walkthrough, modern Chromium reported invalid
identifier patterns because HTML patterns use Unicode-set (`v`) regular
expressions. Product, feature, and skill ID fields now escape the literal
hyphen; a regression test compiles every affected pattern with `v` and checks
valid identifiers, path separators, and uppercase rejection.
The real BA review also exposed Vue's omitted-Boolean-prop behavior:
`canApprove` now explicitly defaults to true, so BA, architecture, and developer
assignment approvals are enabled unless the caller deliberately gates them.
Tests cover each ungated form as well as the existing blocked plan-approval case.

After feedback that approval targets were unclear, the review UI now identifies
the active task, exact document/revision, named reviewer, and consequences of
each action. Current architecture is selected by revision and shown beside its
decision; missing documents disable review actions. Prior documents and history
are collapsed as reference material.

All 45 frontend tests, strict Vue/TypeScript checking, and the production build
passed. Eight added cases cover stage-specific action labels, document links,
reviewer versus final authorization, exact architecture selection, and missing
architecture gating. Isolated Edge browser fixtures verified BA, architecture,
and plan review at both 1440px and 390px: six layouts without horizontal overflow
or JavaScript errors. Document links resolved to the intended artifact, and final
approval stayed disabled pending reviewer responses. These layout checks did
not submit real decisions or modify the completed live run.

### Live UI CSV-export feature

On 2026-10-16, the API resource was refreshed without restarting the workflow
service or database. The real Vue website submitted the sample product,
repository, reviewer, specialist prompt, and CSV-export feature. Run
`ab9a5cb5-1060-4103-9f6a-ae44242b06e2` reached `code_change_ready`.
The user submitted the review decisions in the website, including specification
revision 2, architecture revision 3, and developer/final-plan revision 2.
Approvals were not simulated or submitted automatically.

The published review branch is
`collaborator/feature-ab9a5cb5106041039f6aae44242b06e2`, commit
`371486f222debef466f9402f0798092dd4072fb7`. Independent Git inspection confirmed
its parent is the pinned baseline `4da13dc26c774d9e4c9ad400d354fa204dd9026b`,
the remote `main` remains at that baseline, and task-model code, original tests,
and the dependency manifest are unchanged. Nothing was merged.

Independent verification used a separate checkout of that exact published branch:

- `npm test`: all 13 tests passed, including the four unchanged baseline tests.
- 290 additional exact-byte/immutability cases passed, covering all four formula
  prefixes after whitespace and C0/C1 controls, embedded CRLF, and empty exports.
- Browser forms added comma/quote, formula-like, and Unicode titles and toggled
  task completion against the real generated server.
- An installed Edge browser downloaded `task-export.csv`: six tasks, 211 UTF-8
  bytes matching the endpoint body and byte-count header. Quoting and formula
  protection were correct; the original title stayed unchanged.
- A test-only HTTP 503 interception in a separate browser verified visible inline
  errors, no navigation or task mutation, and successful retry after removing
  interception. All three browser verification phases passed.

The embedded VS Code browser did not expose a download event to automation, so
actual downloaded-file verification used installed Edge instead. The generated
sample remains an in-memory development app, not a deployment. The requested
visible "hello world" addition was explicitly deferred by the approved plan and
is not included in this CSV-export run. The current workflow cannot return from
architecture review to the BA stage to amend an already approved specification.

### Human developer implementation proposals

New Vue submissions offer `proposals-first` and `draft-first` modes. Separate
proposal rounds require every selected human developer to submit implementation
ideas or explicitly confirm no additional input. The last confirmation persists
an outbox message that releases the durable planning wait. Initial drafts are
not approvable and do not create review assignments. New plan revisions prefill
old inputs but require fresh confirmations; frozen rounds are immutable.

Validation on 2026-10-16:

- 52 frontend tests passed, including both submission modes, mode-sensitive retry
  identities, proposal/no-input payloads, failed submissions, fresh confirmation
  of prefilled input, progress, and separation from approval controls.
- 44 focused backend tests passed across developer proposals, existing specialist
  selection, and directory HTTP contracts. Proposal cases cover exact revisions,
  participant membership, text limits, wait-for-all behavior, immutable frozen
  rounds, idempotent confirmation/outbox delivery intent, both starting modes,
  revision reconfirmation, legacy compatibility, and all-input planner grounding.
- A broader related foundation run passed 159 tests. Its filter excluded live
  Copilot calls and the previously documented unrelated
  `RepositorySourceTools_ReadsWholeFilesAndSearchesBeyondOldRepositoryLimits`
  failure; it is not evidence that the entire unfiltered suite passes.
- Isolated Edge fixtures exercised both modes at 1440px and 390px, submitted two
  named contributions per mode, verified explicit no-input handling and progress,
  then displayed the combined plan with separate final approval still gated.
  No real review decisions or feature requests were submitted by these checks.
- API/workflow builds and strict Vue/TypeScript production build succeeded.
  The generated Functions extension project retained the pre-existing NU1900
  warning about unavailable vulnerability metadata.
- API and workflow resources were refreshed through Aspire; both reported healthy.
  The new bundle is served, and proposal API/internal workflow routes reject
  missing keys. Database resources and durable state were not restarted or reset.

These initial checks are local contract/state tests and browser fixtures, not a new
end-to-end live AI acceptance run. Proposal history/settings reuse the existing
artifact schema, so no database migration was needed. Tests used isolated output
directories to avoid locked files in the running services.

### Live automated developer-proposal examples

On 2026-10-16, the console API client completed both planning modes using the
actual Aspire-hosted API, PostgreSQL, durable workflow and Copilot model. The
feature added `GET /api/tasks/summary` and visible total/completed/remaining
counts to an isolated dependency-free Node task-board repository.

| Mode | Run | Confirmed proposal rounds | Final approved plan | Published review commit | Independent repository tests |
|---|---|---|---|---|---|
| `proposals-first` | `e9f0ea4a-ff6a-46b4-834c-87582d31a615` | 1, 2 | 2 | `16376ce3645fc5b2da710c33e47d16c5c0efb08f` | 11 passed |
| `draft-first` | `96550f10-d11a-40df-ad40-dfeb46437029` | 2, 3 | 3 | `e9db78fdbedc7ca6cbb41c1e2114f97c506d3538` | 9 passed |

The review branches are
`collaborator/feature-e9f0ea4aff6a46b4834c87582d31a615` and
`collaborator/feature-96550f10d11a40dfad40dfeb46437029`.
Both commits have the original pinned baseline
`4da13dc26c774d9e4c9ad400d354fa204dd9026b` as their parent. Source and remote
`main` stayed at that baseline; original tests and the dependency manifest were
unchanged. Nothing was merged or deployed.

The first round contained the implementer's idea and the tester's explicit
no-additional-input confirmation. Coordinator feedback requested a revision.
Both developers submitted fresh revised proposals, including explicit Unicode,
empty/mixed/all-complete state, nonmutation, exact JSON-key, and add/toggle
verification requirements. Persisted API history confirmed both rounds were
complete/frozen and both revised plans referenced the developer contributions.
Both reviewers completed each combined-plan review; draft-first revision 1 had
no approval or reviewer assignments.

The initial example invocations omitted scripted developer-review responses and
correctly stopped instead of inventing approvals. The reusable
`scripts/Invoke-DeveloperProposalExample.ps1` now supplies both responses; the
same run IDs were resumed without creating duplicate feature requests. Scripted
proposals and approvals are demonstration inputs, not independent human sign-off.
The script supports `-RunId` and `-SkipRevisionRequest` for explicit resumption.

Independent verification used separate exact-commit review checkouts, not the
workflow's own success status:

- `npm test`: 11 tests passed for proposals-first, 9 for draft-first, including
  all four unchanged baseline tests in each checkout.
- 153 HTTP/count states per branch (306 total): sizes 0 through 16, all-complete,
  all-incomplete and mixed boards, then add and toggle. Each checked exact integer
  JSON keys/values, response type, repeat-read nonmutation, Unicode titles,
  unchanged frozen seed data, existing list behavior, and unknown-route 404s.
- Real headless Edge, against each generated server without fixtures: empty
  initial counts, add, complete and reopen at 1440px and 390px. All four scenarios
  passed with Unicode titles, no page errors, and no horizontal overflow. The
  verifier accommodates the two valid generated summary-element IDs; its first
  draft-first attempt timed out on an overly specific selector, not an app error.
  Verification servers and browsers were closed afterward.
- 52 targeted client/proposal tests passed. The broader related backend/client
  regression run passed 167 tests, excluding live test-category model calls and
  the previously documented unrelated repository source-search failure. This is
  not a claim that the full unfiltered suite passes.
- The example PowerShell script parsed successfully. Three isolated argument
  checks (with `dotnet` mocked, not model calls) verified proposals-first,
  draft-first revision bindings, and resume without repeating a revision request.
  The repository diff passed whitespace checks.

The console now accepts revision-bound `--implementation-proposal` and
`--no-implementation-input` values and handles contribution gates while polling.
Missing any selected developer's scripted input fails before proposal writes;
confirmed inputs can be skipped when attaching with `--run`. Legacy runs still
omit `--planning-mode` and retain their original behavior.

### VS Code AppHost + Vue launch verification

On 2026-10-16, the AppHost build initially reproduced MSB3027/MSB3021 failures:
the previously running API and Functions worker locked dependency DLLs on Windows.
The `Collaborator: AppHost + Vue` compound now runs a scoped Aspire stop task
before launching either member. It does not delete persistent volumes. The stop
command was verified with both an existing AppHost and no running instance.

After stopping the old stack, the normal AppHost build (without reference-assembly
workarounds) succeeded with zero errors. A subsequent build had only two NU1900
warnings from the generated Functions extension project because vulnerability
metadata access was denied. Vue type-check/production build and all 52 frontend
tests passed.

The editor Aspire start tool returned a generic launch failure, so an interactive
F5 compound start has not been verified. Startup with the pinned local Aspire CLI
succeeded, and the rebuilt API and workflows became healthy. The Vue dev server
started with the same command used by the launch configuration. Real headless
Edge connected through its authenticated API proxy, opened the persisted
draft-first example and rendered the activity timeline without page errors.
Both completed example runs remained `code_change_ready` after restart.

The later start/stop report reproduced a separate frontend port conflict: a
detached verification Vite server was still listening on 5173. The compound's Vue
member failed explicitly with `Port 5173 is already in use`, then `stopAll`
terminated the other member. The leftover verification server was stopped by its
own session ID. The exact Vue launch command then started successfully and served
three consecutive HTTP 200 checks. That temporary check server was also stopped
so the next F5 launch can own port 5173. This verifies recovery from the port
conflict, not an interactive compound-debugger launch.

The subsequent 13:30 editor log showed a different failure after successful
AppHost startup: the migrator child debug session ended at 13:30:51.787 and the
AppHost debug session was stopped at 13:30:51.792. The migrator is a one-shot
program that exits after applying migrations. The compound's `stopAll: true`
allowed child-session completion to tear down the stack. It is now explicitly
`false`; normal child completion must not propagate shutdown to the AppHost/Vue.
This supersedes the original all-members-stop-together behavior described above.

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
