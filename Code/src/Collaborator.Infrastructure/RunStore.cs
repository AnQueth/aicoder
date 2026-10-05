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

        var reviewResponses = await db.ReviewAssignments.AsNoTracking()
            .Where(x => x.RunId == id && x.Revision == revision - 1 &&
                x.RespondedAt != null && x.Response != null)
            .OrderBy(x => x.DeveloperId)
            .Select(x => new { x.DeveloperId, x.DeveloperDisplayName, x.Response })
            .ToListAsync(cancellationToken);
        var feedback = new List<string>();
        if (!string.IsNullOrWhiteSpace(previous.Comment))
        {
            feedback.Add($"Coordinator feedback:\n{previous.Comment.Trim()}");
        }
        if (reviewResponses.Count > 0)
        {
            feedback.Add(
                "Developer review feedback:\n" +
                string.Join("\n\n", reviewResponses.Select(x =>
                    $"Reviewer {x.DeveloperDisplayName} ({x.DeveloperId}):\n{x.Response}")));
        }

        return (
            JsonSerializer.Deserialize<DraftPlan>(previous.PlanJson, JsonSerializerOptions.Web),
            feedback.Count == 0 ? null : string.Join("\n\n", feedback));
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
        var developers = await db.Developers.AsNoTracking()
            .Where(x => x.IsEnabled).OrderBy(x => x.UserId).ToListAsync(cancellationToken);
        foreach (var developer in developers)
        {
            db.ReviewAssignments.Add(new ReviewAssignmentRecord
            {
                RunId = id,
                Revision = revision,
                DeveloperId = developer.UserId,
                DeveloperDisplayName = developer.DisplayName,
                ReviewFocus = developer.ReviewFocus,
                ReviewPrompt = BuildReviewPrompt(run, plan, developer)
            });
        }
        AddEvent(id, "plan_generated", "planner");
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildReviewPrompt(FeatureRun run, DraftPlan plan, DeveloperRecord developer)
    {
        var focus = string.IsNullOrWhiteSpace(developer.ReviewFocus)
            ? "implementation feasibility, missing requirements, repository fit, and test coverage"
            : developer.ReviewFocus.Trim();
        var steps = string.Join(Environment.NewLine, plan.Steps.Select((step, index) => $"{index + 1}. {step}"));
        var questions = plan.OpenQuestions.Length == 0
            ? "(none)"
            : string.Join(Environment.NewLine, plan.OpenQuestions.Select(question => $"- {question}"));
        return $"""
            Review this feature plan as {developer.DisplayName} ({developer.UserId}).
            Focus your review on: {focus}.

            Feature request:
            {run.Text}

            Plan summary:
            {plan.Summary}

            Proposed steps:
            {steps}

            Open questions:
            {questions}

            Return concise, actionable feedback identifying risks, omissions, and recommended changes.
            This response is review evidence for the coordinator; it does not itself approve, reject, or revise the plan.
            """;
    }

    public async Task RecordOutcomeAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await GetAsync(id, cancellationToken);
        if (run.Status is "code_change_generating" or "code_change_ready" or "plan_rejected")
        {
            return;
        }

        run.Status = run.Decision switch
        {
            PlanDecisionKinds.Approve => "code_change_generating",
            PlanDecisionKinds.Reject => "plan_rejected",
            _ => throw new InvalidOperationException("A persisted approve or reject decision is required.")
        };
        AddEvent(id, run.Decision == PlanDecisionKinds.Approve ? "plan_approved" : "plan_rejected", "workflow");
        if (run.Decision == PlanDecisionKinds.Approve)
        {
            AddEvent(id, "code_change_started", "workflow");
        }
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
        var codeChangeFailed = run.Decision == PlanDecisionKinds.Approve;
        run.FailureCode = codeChangeFailed ? "code_change_failed" : "workflow_failed";
        var codeChange = await db.CodeChanges.SingleOrDefaultAsync(x => x.RunId == id, cancellationToken);
        if (codeChangeFailed && codeChange is not null)
        {
            codeChange.Status = CodeChangeStatuses.Failed;
        }
        AddEvent(id, run.FailureCode, "workflow");
        await db.SaveChangesAsync(cancellationToken);
    }

    public void AddEvent(Guid id, string kind, string actor) =>
        db.Events.Add(new RunEvent { RunId = id, Kind = kind, Actor = actor });
}
