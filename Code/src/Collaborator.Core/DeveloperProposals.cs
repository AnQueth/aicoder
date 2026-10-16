namespace Collaborator.Core;

/// <summary>Opt-in starting points for collecting human implementation proposals.</summary>
public static class DeveloperPlanningModes
{
    public const string ProposalsFirst = "proposals-first";
    public const string DraftFirst = "draft-first";
    public static bool IsKnown(string? mode) => mode is ProposalsFirst or DraftFirst;
}

/// <summary>A developer's explicit contribution to one target plan revision.</summary>
public sealed record DeveloperProposalSubmission(int Revision, string? Proposal, bool NoAdditionalInput = false);

/// <summary>A snapshotted participant and their proposal, confirmed separately in every round.</summary>
public sealed record DeveloperProposal(
    string UserId, string DisplayName, string[] MatchedRoles, string? ReviewFocus,
    string? Proposal, bool NoAdditionalInput, DateTimeOffset? SubmittedAt);

/// <summary>All human inputs frozen together before the AI generates the target plan.</summary>
public sealed record DeveloperProposalRound(int Revision, string Status, DeveloperProposal[] Developers);

/// <summary>Validates an explicit proposal or no-input response, never an ambiguous empty submission.</summary>
public static class DeveloperProposalRules
{
    public static bool IsValid(DeveloperProposalSubmission submission) =>
        submission.Revision is >= 1 and <= InputRules.MaximumPlanRevisions &&
        submission.Proposal is not { Length: > InputRules.MaximumCommentLength } &&
        (submission.NoAdditionalInput ? string.IsNullOrWhiteSpace(submission.Proposal)
            : InputRules.IsText(submission.Proposal, InputRules.MaximumCommentLength));
}
