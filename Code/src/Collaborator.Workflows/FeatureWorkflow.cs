using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Collaborator.Workflows;

/// <summary>Durable text-to-draft workflow with a persisted, revision-bound human decision loop.</summary>
public sealed class FeatureWorkflow(
    RunStore store,
    Planner planner,
    BusinessAnalyst businessAnalyst,
    Architect architect,
    DeveloperSpecialist developerSpecialist,
    DeveloperCollaborator developerCollaborator,
    DeveloperReviewerSelector reviewerSelector,
    RepositoryCatalogService repositoryCatalog,
    CodeChangeService codeChangeService,
    IConfiguration config,
    ILogger<FeatureWorkflow> logger)
{
    [Function(nameof(BusinessAnalystFeatureWorkflow))]
    public static async Task BusinessAnalystFeatureWorkflow([OrchestrationTrigger] TaskOrchestrationContext context)
        => await RunWorkflow(context, collectDeveloperProposals: false);

    [Function(nameof(DeveloperProposalFeatureWorkflow))]
    public static async Task DeveloperProposalFeatureWorkflow([OrchestrationTrigger] TaskOrchestrationContext context)
        => await RunWorkflow(context, collectDeveloperProposals: true);

    private static async Task RunWorkflow(TaskOrchestrationContext context, bool collectDeveloperProposals)
    {
        var id = context.GetInput<Guid>();
        try
        {
            var specificationRevision = 1;
            while (true)
            {
                await context.CallActivityAsync(
                    nameof(GenerateFeatureSpecification),
                    new PlanGenerationRequest(id, specificationRevision));
                context.SetCustomStatus("awaiting_business_analyst_review");
                var decision = await WaitForBusinessAnalystDecision(context, specificationRevision);
                if (decision.Decision == BusinessAnalystDecisionKinds.Approve)
                {
                    break;
                }
                if (decision.Decision == BusinessAnalystDecisionKinds.Reject)
                {
                    context.SetCustomStatus("feature_request_rejected");
                    return;
                }
                specificationRevision++;
                context.SetCustomStatus("business_analyst_revision_requested");
            }

            var architectureRevision = 1;
            while (true)
            {
                await context.CallActivityAsync(
                    nameof(GenerateArchitectureAnalysis),
                    new PlanGenerationRequest(id, architectureRevision));
                context.SetCustomStatus("awaiting_architecture_review");
                var decision = await WaitForArchitectureDecision(context, architectureRevision);
                if (decision.Decision == ArchitectureAnalysisDecisionKinds.Approve)
                {
                    break;
                }
                if (decision.Decision == ArchitectureAnalysisDecisionKinds.Reject)
                {
                    context.SetCustomStatus("architecture_rejected");
                    return;
                }
                architectureRevision++;
                context.SetCustomStatus("architecture_revision_requested");
            }

            context.SetCustomStatus("developer_skills_processing");
            var developerSkills = await context.CallActivityAsync<AgentSkillRevisionReference[]>(
                nameof(GetDeveloperSkillReferences), id);
            for (var index = 0; index < developerSkills.Length; index++)
            {
                await context.CallActivityAsync(
                    nameof(GenerateDeveloperSkillAnalysis),
                    new DeveloperSkillAnalysisRequest(
                        id, developerSkills[index], index + 1, developerSkills.Length));
            }
            await context.CallActivityAsync(nameof(CompleteDeveloperSkillAnalyses), id);
            context.SetCustomStatus("developer_collaboration_synthesis");
            await context.CallActivityAsync(nameof(GenerateCollaborationSynthesis), id);

            var planRevision = 1;
            if (collectDeveloperProposals)
            {
                var mode = await context.CallActivityAsync<string>(nameof(GetDeveloperPlanningMode), id);
                if (mode == DeveloperPlanningModes.DraftFirst)
                {
                    await context.CallActivityAsync(nameof(GenerateCollaborativePlan), new PlanGenerationRequest(id, planRevision));
                    planRevision++;
                }
            }
            while (true)
            {
                if (collectDeveloperProposals)
                {
                    var proposalRequest = new PlanGenerationRequest(id, planRevision);
                    await context.CallActivityAsync(nameof(PrepareDeveloperProposals), proposalRequest);
                    context.SetCustomStatus("awaiting_developer_proposals");
                    while (true)
                    {
                        var signal = await context.WaitForExternalEvent<PlanGenerationRequest>("developer-proposals-ready");
                        if (signal.RunId == id && signal.Revision == planRevision &&
                            await context.CallActivityAsync<bool>(nameof(AreDeveloperProposalsReady), proposalRequest))
                        {
                            break;
                        }
                    }
                }
                await context.CallActivityAsync(
                    nameof(GenerateCollaborativePlan),
                    new PlanGenerationRequest(id, planRevision));
                context.SetCustomStatus("awaiting_approval");
                var decision = await WaitForDecision(context, planRevision);
                if (decision.Decision == PlanDecisionKinds.Revise)
                {
                    planRevision++;
                    context.SetCustomStatus("revision_requested");
                    continue;
                }
                await context.CallActivityAsync(nameof(RecordOutcome), id);
                if (decision.Decision == PlanDecisionKinds.Approve)
                {
                    context.SetCustomStatus("code_change_generating");
                    await context.CallActivityAsync(nameof(GenerateCodeChange), id);
                    await context.CallActivityAsync(nameof(PrepareReviewBranch), id);
                    await context.CallActivityAsync(nameof(PublishReviewBranch), id);
                    context.SetCustomStatus("code_change_ready");
                }
                else
                {
                    context.SetCustomStatus("decision_recorded");
                }
                return;
            }
        }
        catch (TaskFailedException)
        {
            await context.CallActivityAsync(nameof(RecordFailure), id);
            throw;
        }
    }

    private static async Task<BusinessAnalystDecision> WaitForBusinessAnalystDecision(
        TaskOrchestrationContext context, int revision)
    {
        while (true)
        {
            var decision = await context.WaitForExternalEvent<BusinessAnalystDecision>("business-analyst-decision");
            if (decision.Revision != revision || !BusinessAnalystDecisionKinds.IsKnown(decision.Decision))
            {
                continue;
            }
            if (decision.Decision == BusinessAnalystDecisionKinds.Revise &&
                revision >= InputRules.MaximumPlanRevisions)
            {
                continue;
            }
            return decision;
        }
    }

    private static async Task<ArchitectureDecisionSignal> WaitForArchitectureDecision(
        TaskOrchestrationContext context, int revision)
    {
        while (true)
        {
            var decision = await context.WaitForExternalEvent<ArchitectureDecisionSignal>("architecture-decision");
            if (decision.Revision != revision ||
                !ArchitectureAnalysisDecisionKinds.IsKnown(decision.Decision))
            {
                continue;
            }
            if (decision.Decision == ArchitectureAnalysisDecisionKinds.Revise &&
                revision >= InputRules.MaximumPlanRevisions)
            {
                continue;
            }
            return decision;
        }
    }

    // Stale or duplicate signals (for example outbox redelivery) are ignored unless they bind to the current revision.
    private static async Task<PlanDecisionSignal> WaitForDecision(TaskOrchestrationContext context, int revision)
    {
        while (true)
        {
            var signal = await context.WaitForExternalEvent<PlanDecisionSignal>("plan-decision");
            if (signal.Revision != revision || !PlanDecisionKinds.IsKnown(signal.Decision))
            {
                continue;
            }
            if (signal.Decision == PlanDecisionKinds.Revise && revision >= InputRules.MaximumPlanRevisions)
            {
                continue;
            }
            return signal;
        }
    }

    [Function(nameof(GenerateFeatureSpecification))]
    public async Task GenerateFeatureSpecification(
        [ActivityTrigger] PlanGenerationRequest request, CancellationToken cancellationToken)
    {
        var run = await store.GetAsync(request.RunId, cancellationToken);
        if (run.FeatureSpecificationRevision >= request.Revision)
        {
            return;
        }
        logger.LogInformation(
            "Generating business-analysis specification revision {Revision} for run {RunId}.",
            request.Revision, request.RunId);
        var (previousSpecification, feedback) =
            await store.GetFeatureSpecificationRevisionContextAsync(request.RunId, request.Revision, cancellationToken);
        await using var repositorySnapshot = await repositoryCatalog.TryReadForRunAsync(
            request.RunId, cancellationToken);
        var specification = await businessAnalyst.CreateAsync(
            run.ProductBrief, run.Text, run.BusinessAnalystSkillName, run.BusinessAnalystPrompt,
            previousSpecification, feedback, request.Revision, cancellationToken, repositorySnapshot?.Source);
        await store.StoreFeatureSpecificationAsync(request.RunId, request.Revision, specification, cancellationToken);
    }

    [Function(nameof(GenerateArchitectureAnalysis))]
    public async Task GenerateArchitectureAnalysis(
        [ActivityTrigger] PlanGenerationRequest request, CancellationToken cancellationToken)
    {
        var run = await store.GetAsync(request.RunId, cancellationToken);
        var current = await store.GetCurrentArchitectureArtifactAsync(request.RunId, cancellationToken);
        if (current is not null && current.Revision >= request.Revision)
        {
            return;
        }
        if (!run.BusinessAnalystApproved)
        {
            throw new InvalidOperationException("Architecture analysis requires an approved business specification.");
        }
        var approvedSpecificationRecord = await store.GetFeatureSpecificationRevisionAsync(
            request.RunId, run.FeatureSpecificationRevision, cancellationToken);
        if (approvedSpecificationRecord?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Architecture analysis requires the persisted approved specification.");
        }
        var skill = await store.GetActiveAgentSkillAsync(
            ArchitectureAnalysisSkill.RoleId,
            ArchitectureAnalysisSkill.Domain,
            ArchitectureAnalysisSkill.SkillId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Configure an enabled architect/general/architecture-analysis skill before architecture review.");
        var specification = System.Text.Json.JsonSerializer.Deserialize<FeatureSpecification>(
            approvedSpecificationRecord.SpecificationJson, System.Text.Json.JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The approved feature specification is invalid.");
        var (previousAnalysis, feedback) =
            await store.GetArchitectureRevisionContextAsync(
                request.RunId, request.Revision, cancellationToken);
        var catalog = await repositoryCatalog.GetForRunAsync(request.RunId, cancellationToken);
        await using var repositorySnapshot = await repositoryCatalog.ReadForRunAsync(
            request.RunId, cancellationToken);
        var analysis = await architect.AnalyzeAsync(
            run.ProductBrief,
            run.Text,
            specification,
            skill.Name,
            skill.Prompt,
            previousAnalysis,
            feedback,
            request.Revision,
            catalog,
            repositorySnapshot.Source,
            cancellationToken);
        var inspectedPaths = repositorySnapshot.Source.Paths
            .ToHashSet(StringComparer.Ordinal);
        await store.StoreArchitectureAnalysisAsync(
            request.RunId,
            request.Revision,
            analysis,
            repositorySnapshot.Commit,
            skill.Reference,
            inspectedPaths,
            cancellationToken);
    }

    [Function(nameof(GetDeveloperSkillReferences))]
    public async Task<AgentSkillRevisionReference[]> GetDeveloperSkillReferences(
        [ActivityTrigger] Guid runId, CancellationToken cancellationToken) =>
        (await store.GetDeveloperSkillSnapshotsAsync(runId, cancellationToken))
        .Select(snapshot => snapshot.Skill)
        .ToArray();

    [Function(nameof(GenerateDeveloperSkillAnalysis))]
    public async Task GenerateDeveloperSkillAnalysis(
        [ActivityTrigger] DeveloperSkillAnalysisRequest request, CancellationToken cancellationToken)
    {
        if (await store.HasDeveloperSkillAnalysisAsync(
                request.RunId, request.Skill, cancellationToken))
        {
            return;
        }
        var skill = await store.GetDeveloperSkillSnapshotAsync(
            request.RunId, request.Skill, cancellationToken);
        var run = await store.GetAsync(request.RunId, cancellationToken);
        var architectureArtifact = await store.GetCurrentArchitectureArtifactAsync(
            request.RunId, cancellationToken);
        if (architectureArtifact?.Status != WorkflowArtifactStatuses.Approved ||
            architectureArtifact.Decision != ArchitectureAnalysisDecisionKinds.Approve)
        {
            throw new InvalidOperationException(
                "Developer skill analysis requires human approval of the current architecture analysis.");
        }
        var architecture = System.Text.Json.JsonSerializer.Deserialize<ArchitectureAnalysis>(
            architectureArtifact.PayloadJson, System.Text.Json.JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The approved architecture analysis is invalid.");
        var specificationRecord = await store.GetFeatureSpecificationRevisionAsync(
            request.RunId, run.FeatureSpecificationRevision, cancellationToken);
        if (specificationRecord?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Developer skill analysis requires the persisted approved specification.");
        }
        var specification = System.Text.Json.JsonSerializer.Deserialize<FeatureSpecification>(
            specificationRecord.SpecificationJson, System.Text.Json.JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The approved feature specification is invalid.");
        var catalog = await repositoryCatalog.GetForRunAsync(request.RunId, cancellationToken);
        await using var repositorySnapshot = await repositoryCatalog.ReadForRunAsync(
            request.RunId, cancellationToken);
        logger.LogInformation(
            "Processing developer skill {SkillIndex}/{SkillCount}: {RoleId}/{Domain}/{SkillId} revision {Revision} for run {RunId}.",
            request.SkillIndex, request.SkillCount, skill.Skill.RoleId, skill.Skill.Domain,
            skill.Skill.SkillId, skill.Skill.Revision, request.RunId);
        var analysis = await developerSpecialist.AnalyzeAsync(
            skill,
            run.ProductBrief,
            run.Text,
            specification,
            architecture,
            catalog,
            repositorySnapshot.Source,
            cancellationToken);
        await store.StoreDeveloperSkillAnalysisAsync(
            request.RunId, analysis, repositorySnapshot.Commit, cancellationToken);
    }

    [Function(nameof(CompleteDeveloperSkillAnalyses))]
    public Task CompleteDeveloperSkillAnalyses(
        [ActivityTrigger] Guid runId, CancellationToken cancellationToken) =>
        store.CompleteDeveloperSkillAnalysesAsync(runId, cancellationToken);

    [Function(nameof(GenerateCollaborationSynthesis))]
    public async Task GenerateCollaborationSynthesis(
        [ActivityTrigger] Guid runId, CancellationToken cancellationToken)
    {
        if (await store.GetCollaborationSynthesisAsync(runId, cancellationToken) is not null)
        {
            return;
        }
        var run = await store.GetAsync(runId, cancellationToken);
        var architecture = await store.GetCurrentArchitectureArtifactAsync(runId, cancellationToken);
        if (!run.BusinessAnalystApproved ||
            architecture?.Status != WorkflowArtifactStatuses.Approved ||
            architecture.Decision != ArchitectureAnalysisDecisionKinds.Approve ||
            run.RepositoryCommit is null ||
            architecture.RepositoryCommit != run.RepositoryCommit)
        {
            throw new InvalidOperationException(
                "Cross-role synthesis requires approved inputs from the repository commit pinned to this run.");
        }
        var specification = await store.GetFeatureSpecificationRevisionAsync(
            runId, run.FeatureSpecificationRevision, cancellationToken);
        if (specification?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException(
                "Cross-role synthesis requires the persisted approved feature specification.");
        }
        var analyses = await store.GetDeveloperSkillAnalysesAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Cross-role synthesis requires completed analyses from every captured developer skill.");
        var snapshots = await store.GetDeveloperSkillSnapshotsAsync(runId, cancellationToken);
        if (snapshots.Length == 0)
        {
            logger.LogInformation("Skipping cross-role synthesis for run {RunId}: no specialists selected.", runId);
            return;
        }
        await using var repositorySnapshot = await repositoryCatalog.ReadForRunAsync(runId, cancellationToken);
        var synthesis = await developerCollaborator.SynthesizeAsync(
            analyses,
            snapshots.Select(snapshot => snapshot.Skill).ToArray(),
            cancellationToken,
            repositorySnapshot.Source);
        await store.StoreCollaborationSynthesisAsync(
            runId,
            synthesis,
            repositorySnapshot.Commit,
            cancellationToken);
    }

    [Function(nameof(GenerateCollaborativePlan))]
    public Task GenerateCollaborativePlan(
        [ActivityTrigger] PlanGenerationRequest request, CancellationToken cancellationToken)
        => GeneratePlanAsync(request, cancellationToken);

    [Function(nameof(GetDeveloperPlanningMode))]
    public Task<string?> GetDeveloperPlanningMode([ActivityTrigger] Guid id, CancellationToken ct) =>
        store.GetPlanningModeAsync(id, ct);

    [Function(nameof(PrepareDeveloperProposals))]
    public async Task PrepareDeveloperProposals([ActivityTrigger] PlanGenerationRequest request, CancellationToken ct)
    {
        var run = await store.GetAsync(request.RunId, ct);
        var matches = await store.GetSelectedReviewersAsync(run.Id, ct);
        if (matches is null)
        {
            var record = await store.GetFeatureSpecificationRevisionAsync(run.Id, run.FeatureSpecificationRevision, ct);
            var specification = System.Text.Json.JsonSerializer.Deserialize<FeatureSpecification>(
                record!.SpecificationJson, System.Text.Json.JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("The approved specification is invalid.");
            matches = await reviewerSelector.SelectAsync(run.Text, specification,
                await store.GetEnabledDeveloperProfilesAsync(ct), run.ReviewerCount, ct);
        }
        await store.PrepareProposalRoundAsync(run.Id, request.Revision, matches, ct);
    }

    [Function(nameof(AreDeveloperProposalsReady))]
    public async Task<bool> AreDeveloperProposalsReady([ActivityTrigger] PlanGenerationRequest request, CancellationToken ct) =>
        (await store.GetProposalRoundsAsync(request.RunId, ct))
            .Any(x => x.Revision == request.Revision && x.Status == "ready");

    private async Task GeneratePlanAsync(
        PlanGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var run = await store.GetAsync(request.RunId, cancellationToken);
        if (!run.BusinessAnalystApproved)
        {
            throw new InvalidOperationException("Developer planning requires business analyst approval.");
        }
        var architecture = await store.GetCurrentArchitectureArtifactAsync(request.RunId, cancellationToken);
        if (architecture?.Status != WorkflowArtifactStatuses.Approved ||
            architecture.Decision != ArchitectureAnalysisDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Developer planning requires human approval of the architecture analysis.");
        }
        if (run.PlanRevision >= request.Revision)
        {
            return;
        }
        var specificationRevision = await store.GetFeatureSpecificationRevisionAsync(
            request.RunId, run.FeatureSpecificationRevision, cancellationToken);
        if (specificationRevision?.Decision != BusinessAnalystDecisionKinds.Approve)
        {
            throw new InvalidOperationException("The persisted feature specification is not approved.");
        }
        var (previousPlan, feedback) =
            await store.GetRevisionContextAsync(request.RunId, request.Revision, cancellationToken);
        var specification = System.Text.Json.JsonSerializer.Deserialize<FeatureSpecification>(
            specificationRevision.SpecificationJson, System.Text.Json.JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The approved feature specification is invalid.");
        var catalog = await repositoryCatalog.GetForRunAsync(request.RunId, cancellationToken);
        await using var repositorySnapshot = await repositoryCatalog.ReadForRunAsync(
            request.RunId, cancellationToken);
        var specialistAnalyses = await store.GetDeveloperSkillAnalysesAsync(
            request.RunId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Developer planning requires completed analysis from every captured developer skill.");
        var collaborationSynthesis = await store.GetCollaborationSynthesisAsync(request.RunId, cancellationToken);
        if (specialistAnalyses.Length > 0 && collaborationSynthesis is null)
        {
            throw new InvalidOperationException(
                "Collaborative planning requires a completed, source-linked cross-role synthesis.");
        }
        var reviewerMatches = await store.GetSelectedReviewersAsync(request.RunId, cancellationToken);
        if (reviewerMatches is null)
        {
            var developerProfiles = await store.GetEnabledDeveloperProfilesAsync(cancellationToken);
            reviewerMatches = await reviewerSelector.SelectAsync(
                run.Text, specification, developerProfiles, run.ReviewerCount, cancellationToken);
        }
        var developerProposals = await store.GetDeveloperProposalsForPlanAsync(run.Id, request.Revision, cancellationToken);
        var plan = await planner.CreateAsync(
            run.ProductBrief, run.Text, specification, previousPlan, feedback,
            request.Revision, catalog, repositorySnapshot.Source, cancellationToken,
            specialistAnalyses, collaborationSynthesis, developerProposals);
        await store.StorePlanAsync(request.RunId, request.Revision, plan, cancellationToken, reviewerMatches);
    }

    [Function(nameof(RecordOutcome))]
    public Task RecordOutcome([ActivityTrigger] Guid id, CancellationToken cancellationToken) =>
        store.RecordOutcomeAsync(id, cancellationToken);

    [Function(nameof(GenerateCodeChange))]
    public Task GenerateCodeChange([ActivityTrigger] Guid id, CancellationToken cancellationToken) =>
        codeChangeService.GenerateAsync(id, cancellationToken);

    [Function(nameof(PrepareReviewBranch))]
    public Task PrepareReviewBranch([ActivityTrigger] Guid id, CancellationToken cancellationToken) =>
        codeChangeService.PrepareBranchAsync(id, cancellationToken);

    [Function(nameof(PublishReviewBranch))]
    public Task PublishReviewBranch([ActivityTrigger] Guid id, CancellationToken cancellationToken) =>
        codeChangeService.PublishBranchAsync(id, cancellationToken);

    [Function(nameof(RecordFailure))]
    public Task RecordFailure([ActivityTrigger] Guid id, CancellationToken cancellationToken) =>
        store.RecordFailureAsync(id, cancellationToken);

    [Function(nameof(Start))]
    public async Task<IActionResult> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/runs/{id:guid}/start")] HttpRequest request,
        Guid id, [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(request))
        {
            return new UnauthorizedResult();
        }
        var instanceId = id.ToString("N");
        var existing = await client.GetInstanceAsync(instanceId, cancellation: cancellationToken);
        if (existing is null)
        {
            var mode = await store.GetPlanningModeAsync(id, cancellationToken);
            var workflow = mode is null ? nameof(BusinessAnalystFeatureWorkflow) : nameof(DeveloperProposalFeatureWorkflow);
            await client.ScheduleNewOrchestrationInstanceAsync(workflow, id,
                new StartOrchestrationOptions { InstanceId = instanceId }, cancellationToken);
        }
        return new AcceptedResult();
    }

    [Function(nameof(BusinessAnalystDecisionDelivery))]
    public async Task<IActionResult> BusinessAnalystDecisionDelivery(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/runs/{id:guid}/spec-decision")] HttpRequest request,
        Guid id, [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(request))
        {
            return new UnauthorizedResult();
        }
        var body = await request.ReadFromJsonAsync<DecisionDelivery>(cancellationToken);
        if (body is null || body.Revision < 1)
        {
            return new BadRequestObjectResult(new { error = "Feature specification revision required." });
        }
        var record = await store.GetFeatureSpecificationRevisionAsync(id, body.Revision, cancellationToken);
        if (record?.Decision is null)
        {
            return new ConflictObjectResult(new { error = "Persisted business analyst decision required." });
        }
        await client.RaiseEventAsync(id.ToString("N"), "business-analyst-decision",
            new BusinessAnalystDecision(record.Revision, record.Decision, record.Comment), cancellationToken);
        return new AcceptedResult();
    }

    [Function(nameof(ArchitectureDecisionDelivery))]
    public async Task<IActionResult> ArchitectureDecisionDelivery(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/runs/{id:guid}/architecture-decision")]
        HttpRequest request,
        Guid id, [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(request))
        {
            return new UnauthorizedResult();
        }
        var body = await request.ReadFromJsonAsync<DecisionDelivery>(cancellationToken);
        if (body is null || body.Revision < 1)
        {
            return new BadRequestObjectResult(new { error = "Architecture analysis revision required." });
        }
        var artifact = await store.GetCurrentArchitectureArtifactAsync(id, cancellationToken);
        if (artifact?.Revision != body.Revision || artifact.Decision is null)
        {
            return new ConflictObjectResult(new { error = "Persisted architecture decision required." });
        }
        await client.RaiseEventAsync(id.ToString("N"), "architecture-decision",
            new ArchitectureDecisionSignal(artifact.Revision, artifact.Decision), cancellationToken);
        return new AcceptedResult();
    }

    [Function(nameof(Decision))]
    public async Task<IActionResult> Decision(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/runs/{id:guid}/decision")] HttpRequest request,
        Guid id, [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(request))
        {
            return new UnauthorizedResult();
        }
        var body = await request.ReadFromJsonAsync<DecisionDelivery>(cancellationToken);
        if (body is null || body.Revision < 1)
        {
            return new BadRequestObjectResult(new { error = "Plan revision required." });
        }
        var record = await store.GetRevisionAsync(id, body.Revision, cancellationToken);
        if (record?.Decision is null)
        {
            return new ConflictObjectResult(new { error = "Persisted decision required." });
        }
        await client.RaiseEventAsync(id.ToString("N"), "plan-decision",
            new PlanDecisionSignal(record.Revision, record.Decision), cancellationToken);
        return new AcceptedResult();
    }

    [Function(nameof(DeveloperProposalsReady))]
    public async Task<IActionResult> DeveloperProposalsReady(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/runs/{id:guid}/proposals-ready")]
        HttpRequest request, Guid id, [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(request))
        {
            return new UnauthorizedResult();
        }
        var body = await request.ReadFromJsonAsync<DecisionDelivery>(cancellationToken);
        if (body is null || body.Revision < 1)
        {
            return new BadRequestObjectResult(new { error = "Proposal round revision required." });
        }
        var rounds = await store.GetProposalRoundsAsync(id, cancellationToken);
        if (!rounds.Any(round => round.Revision == body.Revision && round.Status == "ready"))
        {
            return new ConflictObjectResult(new { error = "Every developer must confirm input before planning." });
        }
        await client.RaiseEventAsync(id.ToString("N"), "developer-proposals-ready",
            new PlanGenerationRequest(id, body.Revision), cancellationToken);
        return new AcceptedResult();
    }

    private bool IsAuthorized(HttpRequest request) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(config["Security:WorkflowKey"]!)),
            SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers["X-Workflow-Key"].ToString())));
}

internal sealed record DecisionDelivery(int Revision);
