using Collaborator.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Collaborator.Infrastructure;

/// <summary>Idempotent activity persistence and revision-bound decisions.</summary>
public sealed class RunStore(CollaboratorDb db)
{
    public async Task<FeatureRun> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Runs.SingleAsync(x => x.Id == id, cancellationToken);

    public async Task<PlanRevisionRecord?> GetRevisionAsync(Guid id, int revision, CancellationToken cancellationToken) =>
        await db.PlanRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == id && x.Revision == revision, cancellationToken);

    /// <summary>Loads the prior plan and reviewer feedback that a new revision must address.</summary>
    public async Task<(DraftPlan? PreviousPlan, string? Feedback)> GetRevisionContextAsync(
        Guid id, int revision, CancellationToken cancellationToken)
    {
        if (revision <= 1)
        {
            return (null, null);
        }
        var previous = await db.PlanRevisions.AsNoTracking()
            .SingleAsync(x => x.RunId == id && x.Revision == revision - 1, cancellationToken);
        if (previous.Decision != PlanDecisionKinds.Revise)
        {
            throw new InvalidOperationException("A new plan revision requires a persisted revise decision.");
        }
        return (JsonSerializer.Deserialize<DraftPlan>(previous.PlanJson, JsonSerializerOptions.Web), previous.Comment);
    }

    public async Task StorePlanAsync(Guid id, int revision, DraftPlan plan, CancellationToken cancellationToken)
    {
        InputRules.ValidatePlan(plan);
        var run = await GetAsync(id, cancellationToken);
        if (run.PlanRevision >= revision)
        {
            return;
        }
        if (revision != run.PlanRevision + 1)
        {
            throw new InvalidOperationException("Plan revisions must be stored in sequence.");
        }

        var json = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web);
        run.PlanJson = json;
        run.PlanRevision = revision;
        run.Approved = null;
        run.Decision = null;
        run.DecisionComment = null;
        run.DecisionBy = null;
        run.Status = "awaiting_approval";
        db.PlanRevisions.Add(new PlanRevisionRecord { RunId = id, Revision = revision, PlanJson = json });
        AddEvent(id, "plan_generated", "planner");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordOutcomeAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        if (run.Status is "plan_approved" or "plan_rejected")
        {
            return;
        }

        run.Status = run.Decision switch
        {
            PlanDecisionKinds.Approve => "plan_approved",
            PlanDecisionKinds.Reject => "plan_rejected",
            _ => throw new InvalidOperationException("A persisted approve or reject decision is required.")
        };
        AddEvent(id, run.Status, "workflow");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFailureAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        if (run.Status == "failed")
        {
            return;
        }
        run.Status = "failed";
        run.FailureCode = "workflow_failed";
        AddEvent(id, "workflow_failed", "workflow");
        await db.SaveChangesAsync(cancellationToken);
    }

    public void AddEvent(Guid id, string kind, string actor) =>
        db.Events.Add(new RunEvent { RunId = id, Kind = kind, Actor = actor });
}
