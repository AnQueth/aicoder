namespace Collaborator.Core;

/// <summary>A product with a reviewed brief. Repository grounding is a later increment.</summary>
public sealed record ProductRegistration(string Id, string Name, string Brief);

/// <summary>The requested feature intent, not an approved specification.</summary>
public sealed record FeatureSubmission(string Text);

/// <summary>
/// A human decision bound to an exact plan revision. <see cref="Decision"/> is one of
/// <see cref="PlanDecisionKinds"/>; <see cref="Comment"/> records the rejection reason or revision feedback.
/// </summary>
public sealed record PlanDecision(int Revision, string Decision, string? Comment = null);

/// <summary>Allowed plan decision kinds.</summary>
public static class PlanDecisionKinds
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Revise = "revise";

    public static bool IsKnown(string? kind) => kind is Approve or Reject or Revise;
}

/// <summary>Workflow signal raised after a persisted decision; carries no reviewer text.</summary>
public sealed record PlanDecisionSignal(int Revision, string Decision);

/// <summary>Activity input that requests an exact plan revision.</summary>
public sealed record PlanGenerationRequest(Guid RunId, int Revision);

/// <summary>A structured draft plan produced by the initial compatibility workflow.</summary>
public sealed record DraftPlan(string Summary, string[] Steps, string[] OpenQuestions);

/// <summary>Shared input validation independent of HTTP and model providers.</summary>
public static class InputRules
{
    /// <summary>Maximum accepted feature-text length.</summary>
    public const int MaximumTextLength = 16_000;

    /// <summary>Maximum accepted rejection reason or revision feedback length.</summary>
    public const int MaximumCommentLength = 4_000;

    /// <summary>Bounds orchestration history; further changes require a new feature request.</summary>
    public const int MaximumPlanRevisions = 5;

    /// <summary>
    /// Validates a decision: the kind must be known, and reject or revise must record a bounded reason.
    /// Approval comments are optional but bounded.
    /// </summary>
    public static bool IsValidDecision(PlanDecision? decision, out string error)
    {
        error = string.Empty;
        if (decision is null || decision.Revision < 1 || !PlanDecisionKinds.IsKnown(decision.Decision))
        {
            error = "A revision of at least 1 and a decision of approve, reject, or revise are required.";
            return false;
        }
        if (decision.Decision != PlanDecisionKinds.Approve && !IsText(decision.Comment, MaximumCommentLength))
        {
            error = $"A reason or feedback comment of at most {MaximumCommentLength} characters is required to {decision.Decision}.";
            return false;
        }
        if (decision.Comment is { Length: > MaximumCommentLength })
        {
            error = $"Comments are limited to {MaximumCommentLength} characters.";
            return false;
        }
        return true;
    }

    /// <summary>Checks a nonempty, bounded text value.</summary>
    public static bool IsText(string? text, int maximum = MaximumTextLength) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum;

    /// <summary>Checks a safe, lowercase product identifier.</summary>
    public static bool IsProductId(string? id) =>
        id is { Length: > 0 and <= 80 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Rejects incomplete model results rather than treating them as success.</summary>
    public static void ValidatePlan(DraftPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsText(plan.Summary) || plan.Steps is not { Length: > 0 and <= 50 } ||
            plan.Steps.Any(step => !IsText(step)) || plan.OpenQuestions is null ||
            plan.OpenQuestions.Length > 50 || plan.OpenQuestions.Any(question => !IsText(question)))
        {
            throw new InvalidDataException("Agent output does not satisfy the draft-plan contract.");
        }
    }
}
