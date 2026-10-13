param(
    [string]$BaseUrl = 'http://localhost:5076',
    [string]$ApiKey = $env:COLLABORATOR_API_KEY,
    [string]$RepositoryUrl = $env:COLLABORATOR_PRODUCT_REPOSITORY_URL,
    [int]$TimeoutSeconds = 900,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$started = [Diagnostics.Stopwatch]::StartNew()
$passed = 0
$script:timeoutSeconds = $TimeoutSeconds
$script:phaseName = 'Setup'
$script:exchangeCount = 0
$script:queuePollRecordedRuns = @{}
$script:suppressedQueuePolls = 0
$reportDirectory = Join-Path $PSScriptRoot '..\test-results'
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $reportDirectory "Verify-Local-$((Get-Date).ToString('yyyyMMdd-HHmmss-fff')).md"
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force
$script:reportPath = $ReportPath
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$reportHeader = @"
# Local API smoke-test transcript

- Started: $((Get-Date).ToString('o'))
- Base URL: $BaseUrl
- Repository URL: $RepositoryUrl
- API key: not recorded
- Note: This records HTTP request payloads and response bodies. It does not capture hidden prompts or internal tool messages sent between the workflow and the AI model.

"@
[IO.File]::WriteAllText($script:reportPath, $reportHeader, $utf8WithoutBom)

function Add-TranscriptText([string]$Text) {
    [IO.File]::AppendAllText($script:reportPath, "$Text`r`n", $utf8WithoutBom)
}

function Format-TranscriptBody($Body) {
    if ($null -eq $Body) { return '(empty)' }
    if ($Body -is [byte[]]) { $Body = [Text.Encoding]::UTF8.GetString($Body) }
    if ($Body -isnot [string]) { $Body = [string]$Body }
    if ([string]::IsNullOrWhiteSpace($Body)) { return '(empty)' }
    try {
        $parsed = ConvertFrom-Json -InputObject $Body -Depth 100
        return ConvertTo-Json -InputObject $parsed -Depth 100
    } catch {
        return $Body
    }
}

function Add-HttpExchange(
    [string]$Method,
    [string]$Uri,
    [hashtable]$RequestHeaders,
    $RequestBody,
    $ResponseBody,
    [Nullable[int]]$StatusCode,
    [string]$Failure
) {
    $script:exchangeCount++
    $status = if ($null -eq $StatusCode) { 'No HTTP response' } else { [string]$StatusCode }
    $safeFailure = if ($Failure) { "`r`nRequest failure: $Failure`r`n" } else { '' }
    $safeHeaders = foreach ($headerName in $RequestHeaders.Keys) {
        $headerValue = if ($headerName -match '(?i)authorization|api[-_]?key|token|secret|password') {
            '[REDACTED]'
        } else {
            [string]$RequestHeaders[$headerName]
        }
        "- $headerName`: $headerValue"
    }
    if (-not $safeHeaders) { $safeHeaders = '- None' }
    $maxBacktickRun = 0
    foreach ($body in @($RequestBody, $ResponseBody)) {
        foreach ($match in [regex]::Matches([string]$body, '`+')) {
            $maxBacktickRun = [Math]::Max($maxBacktickRun, $match.Length)
        }
    }
    $fence = [string]::new([char]96, [Math]::Max(3, $maxBacktickRun + 1))
    $section = @"
## HTTP exchange $script:exchangeCount — $($script:phaseName)

- Method: $Method
- URL: $Uri
- Status: $status
$safeFailure
### Request headers

$($safeHeaders -join "`r`n")

### Input

$fence
$(Format-TranscriptBody $RequestBody)
$fence

### Output

$fence
$(Format-TranscriptBody $ResponseBody)
$fence

"@
    Add-TranscriptText $section
}

if (-not $ApiKey) {
    $project = Join-Path $PSScriptRoot '..\src\Collaborator.AppHost\Collaborator.AppHost.csproj'
    $values = (& dotnet user-secrets list --project $project) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $values -notmatch '(?m)^Parameters:api-key = (.+)$') {
        $message = 'Set the AppHost Parameters:api-key secret or COLLABORATOR_API_KEY.'
        Add-TranscriptText "## Setup failed`r`n`r`n$message"
        throw $message
    }
    $ApiKey = $Matches[1].Trim()
}
if (-not $RepositoryUrl -or -not $RepositoryUrl.StartsWith('file://', [StringComparison]::OrdinalIgnoreCase)) {
    $message = 'Set COLLABORATOR_PRODUCT_REPOSITORY_URL to a local file:// Git repository before running this verification.'
    Add-TranscriptText "## Setup failed`r`n`r`n$message"
    throw $message
}
$headers = @{ 'X-Api-Key' = $ApiKey }

function Assert-Status($Response, [int]$Expected, [string]$Name) {
    if ([int]$Response.StatusCode -ne $Expected) {
        Add-TranscriptText "#### Check failed: $Name — expected HTTP $Expected, received $($Response.StatusCode).`r`n"
        throw "$Name failed: expected HTTP $Expected; received $($Response.StatusCode)."
    }
    $script:passed++
    Add-TranscriptText "#### Check passed: $Name — HTTP $Expected.`r`n"
    Write-Host "[$script:passed] $Name succeeded (HTTP $Expected)."
}

function Invoke-Http(
    [string]$Uri,
    [string]$Method = 'GET',
    [string]$Body = $null,
    [hashtable]$RequestHeaders = @{},
    [switch]$OmitTranscript
) {
    $args = @{
        Uri = $Uri; Method = $Method
        SkipHttpErrorCheck = $true; TimeoutSec = 15
    }
    if ($RequestHeaders.Count -gt 0) { $args.Headers = $RequestHeaders }
    if ($null -ne $Body) {
        $args.Body = $Body
        $args.ContentType = 'application/json'
    }
    try {
        $response = Invoke-WebRequest @args
        if (-not $OmitTranscript) {
            Add-HttpExchange $Method $Uri $RequestHeaders $Body $response.Content ([int]$response.StatusCode) ''
        }
        return $response
    } catch {
        Add-HttpExchange $Method $Uri $RequestHeaders $Body '' $null $_.Exception.Message
        throw
    }
}

function Invoke-Api(
    [string]$Path,
    [string]$Method = 'GET',
    $Body = $null,
    $ExtraHeaders = @{},
    [switch]$OmitTranscript
) {
    $requestHeaders = $headers.Clone()
    foreach ($key in $ExtraHeaders.Keys) { $requestHeaders[$key] = $ExtraHeaders[$key] }
    $bodyJson = if ($null -eq $Body) { $null } else { ConvertTo-Json $Body -Depth 10 }
    Invoke-Http "$BaseUrl$Path" $Method $bodyJson $requestHeaders -OmitTranscript:$OmitTranscript
}

function Wait-Status([string]$RunId, [string]$Expected) {
    $attemptCount = [Math]::Ceiling($script:timeoutSeconds / 2)
    $pollCount = 0
    $waitStarted = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 1; $i -le $attemptCount; $i++) {
        $pollCount++
        $recordPoll = -not $script:queuePollRecordedRuns.ContainsKey($RunId)
        if ($recordPoll) { $script:queuePollRecordedRuns[$RunId] = $true }
        else { $script:suppressedQueuePolls++ }
        $response = Invoke-Api "/api/v1/runs/$RunId" -OmitTranscript:(-not $recordPoll)
        if ([int]$response.StatusCode -ne 200) {
            Add-TranscriptText "### Run wait failed`r`n`r`n- Run: $RunId`r`n- Expected status: $Expected`r`n- HTTP status: $($response.StatusCode)`r`n- Polls: $pollCount"
            throw "Run lookup failed: $($response.StatusCode)."
        }
        $run = $response.Content | ConvertFrom-Json
        if ($recordPoll) {
            Write-Host "[wait] run=$RunId action=await-$Expected initial-status=$($run.status) (first queue check; later polls are summarized)"
        }
        if ($run.status -eq $Expected) {
            $waitStarted.Stop()
            $summary = "### Run wait completed`r`n`r`n- Run: $RunId`r`n- Expected status: $Expected`r`n- Final status: $($run.status)`r`n- Queue checks: $pollCount`r`n- Elapsed: $($waitStarted.Elapsed)"
            Add-TranscriptText $summary
            Write-Host "[wait complete] run=$RunId status=$($run.status) checks=$pollCount elapsed=$($waitStarted.Elapsed)."
            return $run
        }
        if ($run.status -in @('failed', 'blocked')) {
            $waitStarted.Stop()
            $summary = "### Run wait failed`r`n`r`n- Run: $RunId`r`n- Expected status: $Expected`r`n- Final status: $($run.status)`r`n- Failure code: $($run.failureCode)`r`n- Queue checks: $pollCount`r`n- Elapsed: $($waitStarted.Elapsed)"
            Add-TranscriptText $summary
            Write-Host "[wait failed] run=$RunId status=$($run.status) failure=$($run.failureCode) checks=$pollCount."
            throw "Run ended in $($run.status): $($run.failureCode)."
        }
        Start-Sleep -Seconds 2
    }
    $waitStarted.Stop()
    Add-TranscriptText "### Run wait timed out`r`n`r`n- Run: $RunId`r`n- Expected status: $Expected`r`n- Queue checks: $pollCount`r`n- Elapsed: $($waitStarted.Elapsed)"
    Write-Host "[wait timed out] run=$RunId expected=$Expected checks=$pollCount elapsed=$($waitStarted.Elapsed)."
    throw "Run $RunId did not reach $Expected within $($script:timeoutSeconds) seconds."
}

try {
    Add-TranscriptText "## Phase 1/3: readiness, skill setup and input validation`r`n"
    $script:phaseName = 'Phase 1/3: readiness, skill setup and input validation'
    Write-Host 'Phase 1/3: readiness, skill setup and input validation.'
    Assert-Status (Invoke-Http "$BaseUrl/health") 200 'Database readiness'
    Assert-Status (Invoke-Http "$BaseUrl/api/v1/products/unknown") 401 'Unauthorized access rejected'
    $analystId = 'smoke-' + [Guid]::NewGuid().ToString('N')
    $skillId = 'requirements-analysis'
    $skillPrompt = 'Clarify user outcomes, observable acceptance criteria, constraints, out-of-scope items, and unresolved questions.'
    $developerSkills = Invoke-Api '/api/v1/agent-skills?roleIdPrefix=developer-'
    Assert-Status $developerSkills 200 'Developer skill listing'
    foreach ($oldSmokeSkill in @($developerSkills.Content | ConvertFrom-Json) |
            Where-Object { $_.roleId -match '^developer-smoke-[0-9a-f]{32}$' -and $_.isEnabled }) {
        Assert-Status (Invoke-Api "/api/v1/agent-skills/$($oldSmokeSkill.roleId)/$($oldSmokeSkill.domain)/$($oldSmokeSkill.skillId)/revisions" 'POST' @{
            name = $oldSmokeSkill.name
            prompt = $oldSmokeSkill.prompt
            isEnabled = $false
            changeReason = 'Disabled a temporary role from an earlier local smoke run.'
        }) 201 "Disable prior smoke skill $($oldSmokeSkill.roleId)"
    }
    Assert-Status (Invoke-Api "/api/v1/business-analysts/$analystId/skills") 200 'BA skill listing'
    Assert-Status (Invoke-Api "/api/v1/business-analysts/$analystId/skills/$skillId" 'PUT' @{
        name = 'Smoke test requirements analysis'; prompt = $skillPrompt; isEnabled = $true
    }) 201 'BA skill configuration'
    $architectureSkills = Invoke-Api '/api/v1/agent-skills?roleId=architect&domain=general'
    Assert-Status $architectureSkills 200 'Architecture skill listing'
    $architectureSkill = @($architectureSkills.Content | ConvertFrom-Json) |
        Where-Object skillId -eq 'architecture-analysis' |
        Select-Object -First 1
    if ($null -eq $architectureSkill -or -not $architectureSkill.isEnabled) {
        Assert-Status (Invoke-Api '/api/v1/agent-skills/architect/general/architecture-analysis/revisions' 'POST' @{
            name = 'Smoke test architecture analysis'
            prompt = 'Assess component impact, repository evidence, options, trade-offs, risks, and open questions for the approved specification.'
            isEnabled = $true; changeReason = 'Configured for local smoke verification.'
        }) 201 'Architecture skill configuration'
    }
    $developerRole = 'developer-smoke'
    $developerSkills = Invoke-Api "/api/v1/agent-skills?roleId=$developerRole&domain=general"
    Assert-Status $developerSkills 200 'Smoke developer skill listing'
    $smokeSkill = @($developerSkills.Content | ConvertFrom-Json) |
        Where-Object skillId -eq 'implementation-analysis' |
        Select-Object -First 1
    if ($null -eq $smokeSkill -or -not $smokeSkill.isEnabled) {
        Assert-Status (Invoke-Api "/api/v1/agent-skills/$developerRole/general/implementation-analysis/revisions" 'POST' @{
            name = 'Smoke test developer analysis'
            prompt = 'Analyze implementation changes, risks, and verification needs from this developer role.'
            isEnabled = $true; changeReason = 'Configured for local smoke verification.'
        }) 201 'Developer skill configuration'
    }
    $reviewers = @(
        @{ id = "$analystId-architecture"; profile = @{
            displayName = 'Smoke Architecture Reviewer'; roles = @('architecture')
            reviewFocus = 'System design and repository fit'; isEnabled = $true
        } },
        @{ id = "$analystId-security"; profile = @{
            displayName = 'Smoke Security Reviewer'; roles = @('security')
            reviewFocus = 'Authorization and data handling'; isEnabled = $true
        } },
        @{ id = "$analystId-testing"; profile = @{
            displayName = 'Smoke Testing Reviewer'; roles = @('testing')
            reviewFocus = 'Acceptance and regression coverage'; isEnabled = $true
        } }
    )
    foreach ($reviewer in $reviewers) {
        Assert-Status (Invoke-Api "/api/v1/developers/$($reviewer.id)" 'PUT' $reviewer.profile) 200 `
            "Reviewer profile $($reviewer.id)"
    }
    $productId = 'smoke-' + [Guid]::NewGuid().ToString('N')
    Assert-Status (Invoke-Api '/api/v1/products' 'POST' @{
        id = $productId; name = 'Smoke test product'; brief = 'Synthetic invoice product for local verification.'
        repositoryUrl = $RepositoryUrl
    }) 201 'Product registration'
    Assert-Status (Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' @{
        text = ' '; businessAnalystId = $analystId; businessAnalystSkillId = $skillId
    }) 400 'Invalid feature rejected'

    Add-TranscriptText "## Phase 2/3: BA, architecture, specialist-skill processing, and plan review`r`n"
    $script:phaseName = 'Phase 2/3: BA, architecture, specialist-skill processing, and plan review'
    Write-Host 'Phase 2/3: BA, architecture, specialist-skill processing, and plan review.'
    $intent = @{
        text = 'Add invoice export with explicit acceptance criteria.'
        businessAnalystId = $analystId; businessAnalystSkillId = $skillId
        reviewerCount = $reviewers.Count
    }
    $idempotency = @{ 'Idempotency-Key' = [Guid]::NewGuid().ToString('N') }
    $submitted = Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' $intent $idempotency
    Assert-Status $submitted 202 'Feature submission'
    $runId = ($submitted.Content | ConvertFrom-Json).runId
    $submittedRun = Invoke-Api "/api/v1/runs/$runId"
    Assert-Status $submittedRun 200 'Submitted run retrieval'
    $developerSkillCount = [int](($submittedRun.Content | ConvertFrom-Json).developerSkillCount)
    if ($developerSkillCount -lt 1) { throw 'Feature submission did not capture any developer skills.' }
    $script:timeoutSeconds = [Math]::Max($TimeoutSeconds, 300 + 180 * $developerSkillCount)
    $duplicate = Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' $intent $idempotency
    Assert-Status $duplicate 202 'Duplicate submission'
    if (($duplicate.Content | ConvertFrom-Json).runId -ne $runId) { throw 'Duplicate submission created a different run.' }
    $differentIntent = @{
        text = 'Different text'; businessAnalystId = $analystId
        businessAnalystSkillId = $skillId; reviewerCount = $reviewers.Count
    }
    Assert-Status (Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' $differentIntent $idempotency) 409 'Changed idempotent payload rejected'
    $run = Wait-Status $runId 'awaiting_business_analyst_review'
    if ($run.featureSpecificationRevision -ne 1 -or $null -eq $run.featureSpecification) {
        throw 'Persisted BA feature specification shape is invalid.'
    }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' @{
        revision = 0; decision = 'approve'
    }) 400 'Invalid BA revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' @{
        revision = 99; decision = 'approve'
    }) 409 'Unknown BA revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' @{
        revision = 1; decision = 'revise'
    }) 400 'BA revise without feedback rejected'
    $specification = $run.featureSpecification
    $specification.problemStatement = 'Updated by the human business analyst during local verification.'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/feature-specifications/1" 'PUT' $specification) 204 'BA specification edit'
    $answers = @($specification.openQuestions | ForEach-Object {
        @{ question = $_; answer = 'Use the default behavior stated in the acceptance criteria for this verification.' }
    })
    $baRevision = @{
        revision = 1
        decision = 'revise'
        comment = 'Clarify the CSV export acceptance criteria.'
        answers = $answers
    }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' $baRevision) 202 'BA revision feedback recorded'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' $baRevision) 202 'Duplicate BA feedback accepted'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' @{
        revision = 1; decision = 'approve'
    }) 409 'Contradictory BA decision rejected'
    $run = Wait-Status $runId 'awaiting_business_analyst_review'
    if ($run.featureSpecificationRevision -ne 2) { throw "Expected BA specification revision 2; received $($run.featureSpecificationRevision)." }
    $specifications = Invoke-Api "/api/v1/runs/$runId/feature-specifications"
    Assert-Status $specifications 200 'Feature specification history retrieval'
    if (@($specifications.Content | ConvertFrom-Json).Count -ne 2) { throw 'Expected two feature specification revisions.' }
    $baApproval = @{ revision = 2; decision = 'approve'; actorId = $analystId }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/business-analyst-decisions" 'POST' $baApproval) 202 'BA approval recorded'
    $run = Wait-Status $runId 'awaiting_architecture_review'
    $architectureHistory = Invoke-Api "/api/v1/runs/$runId/architecture"
    Assert-Status $architectureHistory 200 'Architecture analysis history retrieval'
    $architectureRevisions = @($architectureHistory.Content | ConvertFrom-Json)
    $architecture = $architectureRevisions | Sort-Object revision -Descending | Select-Object -First 1
    if ($null -eq $architecture -or $null -eq $architecture.analysis) {
        throw 'The current architecture analysis was not persisted.'
    }
    if ($architecture.status -ne 'awaiting_review') {
        throw "Expected architecture analysis to await review; received '$($architecture.status)'."
    }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/architecture-decisions" 'POST' @{
        revision = 0; decision = 'approve'
    }) 400 'Invalid architecture revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/architecture-decisions" 'POST' @{
        revision = 99; decision = 'approve'
    }) 409 'Unknown architecture revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/architecture-decisions" 'POST' @{
        revision = $architecture.revision; decision = 'approve'; actorId = $analystId
    }) 202 'Architecture approval recorded'
    $run = Wait-Status $runId 'awaiting_approval'
    if (-not $run.businessAnalystApproved -or $run.planRevision -ne 1 -or $null -eq $run.plan) {
        throw 'The plan was not produced after BA and architecture approvals.'
    }
    $specialistResults = Invoke-Api "/api/v1/runs/$runId/specialist-analyses"
    Assert-Status $specialistResults 200 'Developer specialist analyses retrieval'
    $specialistArtifacts = @($specialistResults.Content | ConvertFrom-Json)
    if ($specialistArtifacts.Count -ne 1 -or
        @($specialistArtifacts[0].analyses).Count -ne $developerSkillCount -or
        @($specialistArtifacts[0].skillRevisions).Count -ne $developerSkillCount) {
        throw "Expected one specialist analysis and skill revision for each of the $developerSkillCount captured skills."
    }
    $collaborationResults = Invoke-Api "/api/v1/runs/$runId/collaboration"
    Assert-Status $collaborationResults 200 'Cross-role collaboration retrieval'
    $collaborationArtifacts = @($collaborationResults.Content | ConvertFrom-Json)
    if ($collaborationArtifacts.Count -ne 1 -or $null -eq $collaborationArtifacts[0].synthesis) {
        throw 'Expected the cross-role collaboration synthesis to be persisted.'
    }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 0; decision = 'approve' }) 400 'Invalid revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 99; decision = 'approve' }) 409 'Unknown revision rejected'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{ revision = 1; decision = 'revise' }) 400 'Revise without feedback rejected'
    $assignments = (Invoke-Api "/api/v1/runs/$runId/assignments").Content | ConvertFrom-Json
    $firstRound = @($assignments | Where-Object revision -eq 1)
    if ($firstRound.Count -ne $reviewers.Count) { throw "Expected $($reviewers.Count) reviewer assignments for revision 1." }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{
        revision = 1; decision = 'approve'
    }) 409 'Coordinator approval blocked before reviews'
    $revisionRequester = $firstRound[0].userId
    foreach ($assignment in $firstRound) {
        $review = if ($assignment.userId -eq $revisionRequester) {
            @{ decision = 'revise'; response = 'Include an export size limit and a large-export test.' }
        } else {
            @{ decision = 'approve'; response = 'No blocking issues from this role.' }
        }
        Assert-Status (Invoke-Api "/api/v1/runs/$runId/assignments/$($assignment.userId)/response" 'POST' $review) 202 `
            "Revision 1 review from $($assignment.userId)"
    }
    $revise = @{ revision = 1; decision = 'revise'; comment = 'Include CSV and PDF formats and a large-export test.' }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $revise) 202 'Revision feedback recorded'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $revise) 202 'Duplicate feedback accepted'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{
        revision = 1; decision = 'reject'; comment = 'Contradicts the recorded decision.'
    }) 409 'Contradictory plan decision rejected'
    $run = Wait-Status $runId 'awaiting_approval'
    while ($run.planRevision -lt 2) { Start-Sleep -Seconds 2; $run = Wait-Status $runId 'awaiting_approval' }
    if ($run.planRevision -ne 2) { throw "Expected plan revision 2; received $($run.planRevision)." }
    $assignments = (Invoke-Api "/api/v1/runs/$runId/assignments").Content | ConvertFrom-Json
    $secondRound = @($assignments | Where-Object revision -eq 2)
    if ($secondRound.Count -ne $reviewers.Count) { throw "Expected $($reviewers.Count) reviewer assignments for revision 2." }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' @{
        revision = 2; decision = 'approve'
    }) 409 'Coordinator approval blocked before unanimous reviews'
    foreach ($assignment in $secondRound) {
        Assert-Status (Invoke-Api "/api/v1/runs/$runId/assignments/$($assignment.userId)/response" 'POST' @{
            decision = 'approve'; response = 'The revised plan addresses the prior feedback.'
        }) 202 "Revision 2 approval from $($assignment.userId)"
    }
    $plans = Invoke-Api "/api/v1/runs/$runId/plans"
    Assert-Status $plans 200 'Plan history retrieval'
    if (@($plans.Content | ConvertFrom-Json).Count -ne 2) { throw 'Expected two plan revisions in history.' }
    $decision = @{ revision = 2; decision = 'reject'; comment = 'Rejected by the developer after reviewing the revised plan.' }
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $decision) 202 'Developer plan rejection recorded'
    Assert-Status (Invoke-Api "/api/v1/runs/$runId/plan-decisions" 'POST' $decision) 202 'Duplicate plan rejection accepted'
    $run = Wait-Status $runId 'plan_rejected'
    if ($null -ne $run.codeChange) { throw 'A rejected plan unexpectedly reached code generation.' }

    Add-TranscriptText "## Phase 3/3: BA rejection blocks planning and audit persistence`r`n"
    $script:phaseName = 'Phase 3/3: BA rejection blocks planning and audit persistence'
    Write-Host 'Phase 3/3: BA rejection blocks planning and audit persistence.'
    $rejectedIntent = @{
        text = 'A second synthetic feature'; businessAnalystId = $analystId; businessAnalystSkillId = $skillId
    }
    $rejected = Invoke-Api "/api/v1/products/$productId/feature-requests" 'POST' $rejectedIntent @{
        'Idempotency-Key' = [Guid]::NewGuid().ToString('N')
    }
    Assert-Status $rejected 202 'Second feature submission'
    $rejectedId = ($rejected.Content | ConvertFrom-Json).runId
    $null = Wait-Status $rejectedId 'awaiting_business_analyst_review'
    Assert-Status (Invoke-Api "/api/v1/runs/$rejectedId/business-analyst-decisions" 'POST' @{
        revision = 1; decision = 'reject'; comment = 'Out of scope for this release.'; actorId = $analystId
    }) 202 'BA rejection recorded'
    $rejectedRun = Wait-Status $rejectedId 'feature_request_rejected'
    if ($rejectedRun.planRevision -ne 0 -or $null -ne $rejectedRun.plan) {
        throw 'A BA-rejected specification unexpectedly reached plan generation.'
    }
    $audit = Invoke-Api "/api/v1/runs/$runId/events"
    Assert-Status $audit 200 'Audit retrieval'
    $events = @($audit.Content | ConvertFrom-Json)
    $actualKinds = @($events | ForEach-Object kind)
    foreach ($expectedKind in @(
        'submitted', 'business_analyst_draft_generated', 'business_analyst_specification_edited',
        'business_analyst_revision_requested', 'business_analyst_approved',
        'architecture_analysis_generated', 'architecture_approved',
        'plan_generated', 'revision_requested', 'plan_rejected'
    )) {
        if ($expectedKind -notin $actualKinds) { throw "Audit trail is missing '$expectedKind'." }
    }
    Write-Host "Summary: processed=$passed succeeded=$passed failed=0 skipped=0 elapsed=$($started.Elapsed)."
    Write-Host "Synthetic persisted test product: $productId"
    Add-TranscriptText @"
## Result

- Status: PASSED
- Processed: $passed
- Succeeded: $passed
- Failed: 0
- Skipped: 0
- Elapsed: $($started.Elapsed)
- Synthetic product ID: $productId
- HTTP exchanges recorded: $script:exchangeCount
- Repeated queue polls omitted: $script:suppressedQueuePolls
"@
    Write-Host "Transcript: $script:reportPath"
} catch {
    Write-Host "Summary: processed=$($passed + 1) succeeded=$passed failed=1 skipped=0 elapsed=$($started.Elapsed)."
    Add-TranscriptText @"
## Result

- Status: FAILED
- Processed: $($passed + 1)
- Succeeded: $passed
- Failed: 1
- Skipped: 0
- Elapsed: $($started.Elapsed)
- Error: $($_.Exception.Message)
- HTTP exchanges recorded: $script:exchangeCount
- Repeated queue polls omitted: $script:suppressedQueuePolls
"@
    Write-Host "Transcript: $script:reportPath"
    throw
}
