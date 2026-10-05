param(
    [string]$BaseUrl = 'http://localhost:5076',
    [string]$ApiKey = $env:COLLABORATOR_API_KEY
)

$ErrorActionPreference = 'Stop'
if (-not $ApiKey) {
    $project = Join-Path $PSScriptRoot '..\src\Collaborator.AppHost\Collaborator.AppHost.csproj'
    $values = (& dotnet user-secrets list --project $project) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $values -notmatch '(?m)^Parameters:api-key = (.+)$') {
        throw 'Set the AppHost Parameters:api-key secret or COLLABORATOR_API_KEY.'
    }
    $ApiKey = $Matches[1].Trim()
}
$headers = @{ 'X-Api-Key' = $ApiKey }
$started = [Diagnostics.Stopwatch]::StartNew()
$passed = 0

function Assert-Status($Response, [int]$Expected, [string]$Name) {
    if ([int]$Response.StatusCode -ne $Expected) {
        throw "$Name failed: expected HTTP $Expected; received $($Response.StatusCode)."
    }
    $script:passed++
    Write-Host "[$script:passed] $Name succeeded (HTTP $Expected)."
}

function Invoke-Api([string]$Path, [string]$Method = 'GET', $Body = $null, $ExtraHeaders = @{}) {
    $requestHeaders = $headers.Clone()
    foreach ($key in $ExtraHeaders.Keys) { $requestHeaders[$key] = $ExtraHeaders[$key] }
    $args = @{
        Uri = "$BaseUrl$Path"; Method = $Method; Headers = $requestHeaders
        SkipHttpErrorCheck = $true; TimeoutSec = 15
    }
    if ($null -ne $Body) {
        $args.Body = ConvertTo-Json $Body -Depth 10
        $args.ContentType = 'application/json'
    }
    Invoke-WebRequest @args
}

function Wait-Status([string]$RunId, [string]$Expected) {
    for ($i = 1; $i -le 60; $i++) {
        $response = Invoke-Api "/api/v1/runs/$RunId"
        if ([int]$response.StatusCode -ne 200) { throw "Run lookup failed: $($response.StatusCode)." }
        $run = $response.Content | ConvertFrom-Json
        Write-Host "[wait $i/60] run=$RunId action=await-$Expected status=$($run.status)"
        if ($run.status -eq $Expected) { return $run }
        if ($run.status -in @('failed', 'blocked')) { throw "Run ended in $($run.status): $($run.failureCode)." }
        Start-Sleep -Seconds 2
    }
    throw "Run $RunId did not reach $Expected."
}

try {
    Write-Host 'Phase 1/3: readiness and input validation.'
    Assert-Status (Invoke-WebRequest "$BaseUrl/health" -SkipHttpErrorCheck) 200 'Database readiness'
    Assert-Status (Invoke-WebRequest "$BaseUrl/api/v1/products/unknown" -SkipHttpErrorCheck) 401 'Unauthorized access rejected'
    $productId = 'smoke-' + [Guid]::NewGuid().ToString('N')
    Assert-Status (Invoke-Api '/api/v1/products' 'POST' @{
        id = $productId; name = 'Smoke test product'; brief = 'Synthetic invoice product for local verification.'
    }) 201 'Product registration'
    Assert-Status (Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' @{ text = ' ' }) 400 'Invalid feature rejected'

    Write-Host 'Phase 2/3: durable planning and revision-bound approval.'
    $intent = @{ text = 'Add invoice export with explicit acceptance criteria.' }
    $idempotency = @{ 'Idempotency-Key' = [Guid]::NewGuid().ToString('N') }
    $submitted = Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' $intent $idempotency
    Assert-Status $submitted 202 'Feature submission'
    $runId = ($submitted.Content | ConvertFrom-Json).runId
    $duplicate = Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' $intent $idempotency
    Assert-Status $duplicate 202 'Duplicate submission'
    if (($duplicate.Content | ConvertFrom-Json).runId -ne $runId) { throw 'Duplicate submission created a different run.' }
    Assert-Status (Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' @{ text = 'Different text' } $idempotency) 409 'Changed idempotent payload rejected'
    $run = Wait-Status $runId 'awaiting_approval'
    if ($run.planRevision -ne 1 -or $null -eq $run.plan) { throw 'Persisted plan shape is invalid.' }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 0; decision = 'approve' }) 400 'Invalid revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 99; decision = 'approve' }) 409 'Unknown revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 1; decision = 'revise' }) 400 'Revise without feedback rejected'
    $revise = @{ revision = 1; decision = 'revise'; comment = 'Include CSV and PDF formats and a large-export test.' }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $revise) 202 'Revision feedback recorded'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $revise) 202 'Duplicate feedback accepted'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 1; decision = 'approve' }) 409 'Contradictory decision rejected'
    $run = Wait-Status $runId 'awaiting_approval'
    while ($run.planRevision -lt 2) { Start-Sleep -Seconds 2; $run = Wait-Status $runId 'awaiting_approval' }
    if ($run.planRevision -ne 2) { throw "Expected plan revision 2; received $($run.planRevision)." }
    $plans = Invoke-Api "/api/v1/runs/$runId/plans"
    Assert-Status $plans 200 'Plan history retrieval'
    if (@($plans.Content | ConvertFrom-Json).Count -ne 2) { throw 'Expected two plan revisions in history.' }
    $decision = @{ revision = 2; decision = 'approve' }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $decision) 202 'Revised plan approval recorded'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $decision) 202 'Duplicate approval accepted'
    $null = Wait-Status $runId 'plan_approved'

    Write-Host 'Phase 3/3: rejection and audit persistence.'
    $rejected = Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' @{ text = 'A second synthetic feature' } @{ 'Idempotency-Key' = [Guid]::NewGuid().ToString('N') }
    Assert-Status $rejected 202 'Second feature submission'
    $rejectedId = ($rejected.Content | ConvertFrom-Json).runId
    $null = Wait-Status $rejectedId 'awaiting_approval'
    Assert-Status (Invoke-Api "/api/v1/runs/$rejectedId/plan-decisions" 'POST' @{ revision = 1; decision = 'reject'; comment = 'Out of scope for this release.' }) 202 'Rejection recorded'
    $null = Wait-Status $rejectedId 'plan_rejected'
    $audit = Invoke-Api "/api/v1/runs/$runId/events"
    Assert-Status $audit 200 'Audit retrieval'
    $events = @($audit.Content | ConvertFrom-Json)
    $expectedKinds = 'submitted,plan_generated,revision_requested,plan_generated,decision_recorded,plan_approved'
    $actualKinds = ($events | ForEach-Object kind) -join ','
    if ($actualKinds -ne $expectedKinds) {
        throw "Unexpected audit trail. Expected $expectedKinds; received $actualKinds."
    }
    Write-Host "Summary: processed=$passed succeeded=$passed failed=0 skipped=0 elapsed=$($started.Elapsed)."
    Write-Host "Synthetic persisted test product: $productId"
} catch {
    Write-Host "Summary: processed=$($passed + 1) succeeded=$passed failed=1 skipped=0 elapsed=$($started.Elapsed)."
    throw
}
