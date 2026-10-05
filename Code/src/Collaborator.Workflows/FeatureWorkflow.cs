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
    RepositoryCatalogService repositoryCatalog,
    CodeChangeService codeChangeService,
    IConfiguration config,
    ILogger<FeatureWorkflow> logger)
{
    [Function(nameof(FeaturePlanWorkflow))]
    public static async Task FeaturePlanWorkflow([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var id = context.GetInput<Guid>();
        try
        {
            var revision = 1;
            while (true)
            {
                await context.CallActivityAsync(nameof(GeneratePlan), new PlanGenerationRequest(id, revision));
                context.SetCustomStatus("awaiting_approval");
                var signal = await WaitForDecision(context, revision);
                if (signal.Decision == PlanDecisionKinds.Revise)
                {
                    revision++;
                    context.SetCustomStatus("revision_requested");
                    continue;
                }
                await context.CallActivityAsync(nameof(RecordOutcome), id);
                if (signal.Decision == PlanDecisionKinds.Approve)
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

    [Function(nameof(GeneratePlan))]
    public async Task GeneratePlan([ActivityTrigger] PlanGenerationRequest request, CancellationToken cancellationToken)
    {
        var run = await store.GetAsync(request.RunId, cancellationToken);
        if (run.PlanRevision >= request.Revision)
        {
            return;
        }
        logger.LogInformation("Generating draft plan revision {Revision} for run {RunId}.", request.Revision, request.RunId);
        var catalog = await repositoryCatalog.GetForRunAsync(request.RunId, cancellationToken);
        var (previousPlan, feedback) = await store.GetRevisionContextAsync(request.RunId, request.Revision, cancellationToken);
        var plan = await planner.CreateAsync(
            run.ProductBrief, run.Text, previousPlan, feedback, request.Revision, catalog, cancellationToken);
        await store.StorePlanAsync(request.RunId, request.Revision, plan, cancellationToken);
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
            await client.ScheduleNewOrchestrationInstanceAsync(nameof(FeaturePlanWorkflow), id,
                new StartOrchestrationOptions { InstanceId = instanceId }, cancellationToken);
        }
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

    private bool IsAuthorized(HttpRequest request) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(config["Security:WorkflowKey"]!)),
            SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers["X-Workflow-Key"].ToString())));
}

internal sealed record DecisionDelivery(int Revision);
