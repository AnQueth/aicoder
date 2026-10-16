param(
    [Parameter(Mandatory)]
    [string]$RepositoryUrl,
    [string]$BaseBranch = 'main',
    [ValidateSet('proposals-first', 'draft-first')]
    [string]$PlanningMode = 'proposals-first',
    [string]$BaseUrl = 'http://localhost:5076',
    [string]$ProductId = "proposal-example-$((Get-Date).ToString('yyyyMMdd-HHmmss'))",
    [int]$TimeoutSeconds = 1200,
    [Nullable[guid]]$RunId,
    [switch]$SkipRevisionRequest
)

$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
$firstRevision = 1
if ($PlanningMode -eq 'draft-first') {
    $firstRevision = 2
}
$nextRevision = $firstRevision + 1
$clientProject = Join-Path $PSScriptRoot '..\src\Collaborator.ApiClient'
$feature = @'
In this small Node task-board application add GET /api/tasks/summary returning
{ total, completed, remaining } as integer counts of the current in-memory tasks.
Show those counts on the page on initial load and after add/toggle, while retaining
all existing behavior. Use no new dependencies. Cover empty, mixed and all-complete
boards plus updates after add/toggle with node --test and document usage.
For this automated developer-proposal example, select the two human reviewers
proposal-example-implementer and proposal-example-tester, whose roles are
proposal-example-node and proposal-example-qa. Keep these stable across revisions.
'@
$clientArgs = @(
    '--auto', '--base-url', $BaseUrl, '--timeout', "$TimeoutSeconds",
    '--product', $ProductId, '--product-name', 'Developer proposal API example',
    '--brief', 'Dependency-free Node task-board example; preserve add/list/toggle behavior.',
    '--repository-url', $RepositoryUrl, '--base-branch', $BaseBranch,
    '--reviewer-count', '2',
    '--developer', 'proposal-example-implementer|Proposal example implementer|proposal-example-node|Implement the Node HTTP route and browser counts',
    '--developer', 'proposal-example-tester|Proposal example tester|proposal-example-qa|Test exact counts and preserve task behavior',
    '--review-response', 'proposal-example-implementer=approve',
    '--review-response', 'proposal-example-tester=approve',
    '--feature', $feature,
    '--implementation-proposal', "$firstRevision|proposal-example-implementer|Compute counts from a fresh board.list() snapshot without mutating tasks. Add the GET route and update counts inside the existing browser render function after every add or toggle.",
    '--no-implementation-input', "$firstRevision|proposal-example-tester",
    '--implementation-proposal', "$nextRevision|proposal-example-implementer|Use a pure summary of a fresh board.list() snapshot without mutating tasks. Show Total, Completed, Remaining below the heading and update inside existing render after add/toggle. Derive browser counts from already fetched tasks. Place summary GET before the fallback. Include Unicode titles.",
    '--implementation-proposal', "$nextRevision|proposal-example-tester|Add node --test cases for empty, mixed and all-complete boards, updates after add/toggle, Unicode titles, exact JSON keys, and nonmutation. Preserve all baseline tests unchanged and put new tests in summary.test.js.",
    '--approve'
)
if (-not $SkipRevisionRequest) {
    $clientArgs += @('--feedback', 'Make verification explicit for Unicode titles, empty state and nonmutation. Put counts below the heading, derive browser counts from the already fetched task snapshot, and place summary GET before the route fallback.')
}
if ($null -ne $RunId) {
    $clientArgs += @('--run', "$RunId")
}
else {
    $clientArgs += @('--no-specialists', '--planning-mode', $PlanningMode)
}

Write-Output "Phase 1/2: Automated API example, mode=$PlanningMode, product=$ProductId"
Write-Output "Human proposals and approvals below are scripted demonstration inputs, not independent human sign-off."
Write-Output 'Uses the existing API key from environment/user secrets without printing it. Publishes a review branch only.'
$result = 1
try {
    & dotnet run --project $clientProject -- @clientArgs
    $result = $LASTEXITCODE
    if ($result -ne 0) {
        throw "Automated client exited $result. Use its run ID to resume; do not silently start a duplicate."
    }
    Write-Output 'Phase 2/2: Client completed. Independently inspect the reported commit and run the generated repository tests before merging.'
}
finally {
    $succeeded = [int]($result -eq 0)
    Write-Output "Summary: processed=1 succeeded=$succeeded failed=$([int]($result -ne 0)) skipped=0 elapsed=$($clock.Elapsed)"
}
