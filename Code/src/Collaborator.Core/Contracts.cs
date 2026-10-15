namespace Collaborator.Core;

/// <summary>A product with a reviewed brief and an optional source repository.</summary>
public sealed record ProductRegistration(
    string Id, string Name, string Brief, string? RepositoryUrl = null, string? RepositoryBaseBranch = null);

/// <summary>Changes the optional Git repository configured for a product.</summary>
public sealed record RepositorySourceUpdate(string? RepositoryUrl, string? RepositoryBaseBranch = null);

/// <summary>The requested feature intent and the analyst skill to use, not an approved specification.</summary>
public sealed record FeatureSubmission(
    string Text,
    string BusinessAnalystId = "default",
    string BusinessAnalystSkillId = "requirements-analysis",
    int ReviewerCount = 3);

/// <summary>A versioned business-analysis skill prompt owned by one analyst.</summary>
public sealed record BusinessAnalystSkillConfiguration(string Name, string Prompt, bool IsEnabled = true);

/// <summary>A new human-authored revision of a skill used by one AI role and optional domain.</summary>
public sealed record AgentSkillRevisionRequest(
    string Name, string Prompt, bool IsEnabled = true, string? ChangeReason = null, string? ActorId = null);

/// <summary>Identifies the exact skill revision used to create a workflow artifact.</summary>
public sealed record AgentSkillRevisionReference(
    string RoleId, string SkillId, string Domain, int Revision);

/// <summary>A structured feature specification awaiting business analyst approval.</summary>
public sealed record FeatureSpecification(
    string Title,
    string ProblemStatement,
    string[] UserStories,
    string[] AcceptanceCriteria,
    string[] Constraints,
    string[] OutOfScope,
    string[] OpenQuestions);

/// <summary>An approve, revise, or reject decision bound to one feature-specification revision.</summary>
public sealed record BusinessAnalystDecision(
    int Revision, string Decision, string? Comment = null, string? ActorId = null,
    BusinessAnalystQuestionAnswer[]? Answers = null);

/// <summary>An analyst's answer to one exact open question in a specification revision.</summary>
public sealed record BusinessAnalystQuestionAnswer(string Question, string Answer);

/// <summary>Allowed business analyst decision kinds.</summary>
public static class BusinessAnalystDecisionKinds
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Revise = "revise";

    public static bool IsKnown(string? kind) => kind is Approve or Reject or Revise;
}

/// <summary>
/// A human decision bound to an exact plan revision. <see cref="Decision"/> is one of
/// <see cref="PlanDecisionKinds"/>; <see cref="Comment"/> records the rejection reason or revision feedback.
/// </summary>
public sealed record PlanDecision(int Revision, string Decision, string? Comment = null, string? ActorId = null);

/// <summary>A configured developer profile with one or more selectable engineering roles.</summary>
public sealed record DeveloperConfiguration(
    string DisplayName, string? ReviewFocus = null, bool IsEnabled = true, string[]? Roles = null);

/// <summary>A developer's explicit decision and optional feedback on one plan revision.</summary>
public sealed record ReviewAssignmentResponse(string Decision, string? Response = null);

/// <summary>A role-based reviewer selection made for a feature run.</summary>
public sealed record DeveloperReviewerMatch(string UserId, string[] MatchedRoles);

/// <summary>A configured developer profile supplied to the reviewer selector.</summary>
public sealed record DeveloperConfigurationProfile(
    string UserId, string DisplayName, string[] Roles, string? ReviewFocus);

/// <summary>Allowed decisions on an individual developer review assignment.</summary>
public static class DeveloperReviewDecisionKinds
{
    public const string Approve = "approve";
    public const string Revise = "revise";

    public static bool IsKnown(string? kind) => kind is Approve or Revise;
}

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

/// <summary>A text file included as untrusted evidence for offline analysis and tests.</summary>
public sealed record RepositoryFileEvidence(string Path, string Content);

/// <summary>A read-only handle to text files at one pinned commit in a temporary Git clone.</summary>
public sealed record RepositorySourceReference(string GitDirectory, string Commit, string[] Paths);

/// <summary>A repository capability with locations that can be verified against inspected files.</summary>
public sealed record RepositoryFunctionality(string Name, string Description, string[] Paths, string[] Symbols);

/// <summary>A validated repository catalog pinned to one Git commit.</summary>
public sealed record RepositoryCatalog(
    string Commit,
    DateTimeOffset GeneratedAt,
    int FilesAnalyzed,
    RepositoryFunctionality[] Functionalities,
    int FormatVersion = 0)
{
    public const int CurrentFormatVersion = 2;
}

/// <summary>A requested add, update, or delete in the proposed implementation patch.</summary>
public sealed record CodeFileChange(string Path, string Operation, string? Content);

/// <summary>A bounded implementation patch generated for an approved plan.</summary>
public sealed record CodeChangeProposal(string Summary, CodeFileChange[] Files);

/// <summary>Review metadata for the implementation branch; patch contents remain private.</summary>
public sealed record CodeChangeStatus(
    string Status,
    string? Summary,
    string? BranchName,
    string? Commit,
    string[] ChangedFiles,
    string? FailureCode);

/// <summary>Allowed generated file operations.</summary>
public static class CodeFileOperations
{
    public const string Add = "add";
    public const string Update = "update";
    public const string Delete = "delete";
}

/// <summary>Durable implementation-generation progress values.</summary>
public static class CodeChangeStatuses
{
    public const string PatchGenerated = "patch_generated";
    public const string BranchPrepared = "branch_prepared";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>A structured draft implementation plan.</summary>
public sealed record DraftPlan(string Summary, string[] Steps, string[] OpenQuestions);

/// <summary>A source location that supports an architecture finding.</summary>
public sealed record ArchitectureEvidence(string Path, string? Symbol, int? StartLine, int? EndLine, string Claim);

/// <summary>An affected product component and the repository evidence behind the assessment.</summary>
public sealed record ArchitectureComponentImpact(
    string Component, string Impact, ArchitectureEvidence[] Evidence);

/// <summary>A possible architecture choice and its trade-offs.</summary>
public sealed record ArchitectureOption(
    string Name, string Description, string[] Benefits, string[] Tradeoffs);

/// <summary>A possible consequential decision that a human may choose to record as an ADR.</summary>
public sealed record ArchitectureDecisionProposal(
    string Title, string Context, string[] Options, string Recommendation, string Rationale);

/// <summary>Architecture analysis grounded in one product and repository revision.</summary>
public sealed record ArchitectureAnalysis(
    string Summary,
    ArchitectureComponentImpact[] ComponentImpacts,
    ArchitectureOption[] Options,
    string[] Risks,
    string[] OpenQuestions,
    ArchitectureDecisionProposal[] AdrProposals);

/// <summary>A human decision bound to one exact architecture analysis revision.</summary>
public sealed record ArchitectureAnalysisDecision(
    int Revision, string Decision, string? Comment = null, string? ActorId = null);

/// <summary>Allowed human decisions on an architecture analysis.</summary>
public static class ArchitectureAnalysisDecisionKinds
{
    public const string Approve = "approve";
    public const string Revise = "revise";
    public const string Reject = "reject";

    public static bool IsKnown(string? kind) => kind is Approve or Revise or Reject;
}

/// <summary>Revision-bound decision delivered to the architecture workflow.</summary>
public sealed record ArchitectureDecisionSignal(int Revision, string Decision);

/// <summary>A technical approach generated by a role-specific developer skill.</summary>
public sealed record DeveloperSkillAnalysis(
    string RoleId,
    string Domain,
    string SkillId,
    string SkillName,
    int SkillRevision,
    string Summary,
    string[] ProposedChanges,
    string[] Risks,
    string[] VerificationNeeds,
    string[] OpenQuestions);

/// <summary>An enabled developer skill revision captured when a run is submitted.</summary>
public sealed record DeveloperSkillSnapshot(
    AgentSkillRevisionReference Skill, string Name, string Prompt);

/// <summary>Durable activity input for one exact captured developer skill revision.</summary>
public sealed record DeveloperSkillAnalysisRequest(
    Guid RunId, AgentSkillRevisionReference Skill, int SkillIndex, int SkillCount);

/// <summary>Provider output before the application attaches trusted skill provenance.</summary>
public sealed record DeveloperSkillDraft(
    string Summary,
    string[] ProposedChanges,
    string[] Risks,
    string[] VerificationNeeds,
    string[] OpenQuestions);

/// <summary>A cross-role point that links back to the exact skill revisions that raised it.</summary>
public sealed record CollaborationFinding(
    string Finding, AgentSkillRevisionReference[] Sources);

/// <summary>An unresolved question linked to the specialist skill revisions that raised it.</summary>
public sealed record CollaborationQuestion(
    string Question, AgentSkillRevisionReference[] Sources);

/// <summary>A synthesis of role outputs that preserves agreements, conflicts, and open questions.</summary>
public sealed record CollaborationSynthesis(
    string Summary,
    CollaborationFinding[] Agreements,
    CollaborationFinding[] Conflicts,
    CollaborationQuestion[] UnresolvedQuestions);

/// <summary>A persisted cross-role synthesis and the inputs that produced it.</summary>
public sealed record CollaborationSynthesisArtifactResponse(
    int Revision,
    string Status,
    CollaborationSynthesis Synthesis,
    string InputArtifactRevisionsJson,
    AgentSkillRevisionReference[] SkillRevisions,
    string? RepositoryCommit,
    DateTimeOffset CreatedAt);

/// <summary>A bounded implementation unit proposed from the reviewed feature and technical analysis.</summary>
public sealed record ProposedWorkItem(
    string Id,
    string Title,
    string Description,
    string[] AcceptanceCriteria,
    string[] Dependencies,
    string[] AffectedComponents,
    string[] VerificationNeeds);

/// <summary>A validated work-item set with dependency ordering and known blockers.</summary>
public sealed record WorkItemDecomposition(
    ProposedWorkItem[] Items,
    string[] BlockingQuestions);

/// <summary>A human question attached to a specific workflow artifact revision.</summary>
public sealed record StageQuestion(
    int Index, string Question, string? Answer = null, string? AnsweredBy = null);

/// <summary>A reviewer decision bound to one exact stage artifact.</summary>
public sealed record StageArtifactReview(
    string Decision, string? Comment = null, string? ReviewerId = null);

/// <summary>Stages that produce reviewable workflow artifacts.</summary>
public static class WorkflowStages
{
    public const string BusinessAnalysis = "business_analysis";
    public const string Architecture = "architecture";
    public const string SpecialistAnalysis = "specialist_analysis";
    public const string Synthesis = "synthesis";
    public const string WorkItems = "work_items";
    public const string Planning = "planning";
}

/// <summary>Default registry identity used by the architecture-review stage.</summary>
public static class ArchitectureAnalysisSkill
{
    public const string RoleId = "architect";
    public const string Domain = "general";
    public const string SkillId = "architecture-analysis";
}

/// <summary>Artifact lifecycle states; a stale or superseded result cannot authorize later work.</summary>
public static class WorkflowArtifactStatuses
{
    public const string Draft = "draft";
    public const string Completed = "completed";
    public const string AwaitingReview = "awaiting_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
    public const string Stale = "stale";
}

/// <summary>Shared input validation independent of HTTP and model providers.</summary>
public static class InputRules
{
    /// <summary>Validates and attaches the trusted identity of one captured developer skill.</summary>
    public static DeveloperSkillAnalysis CreateDeveloperSkillAnalysis(
        DeveloperSkillSnapshot skill, DeveloperSkillDraft draft)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(draft);
        if (skill.Skill is null ||
            !IsProductId(skill.Skill.RoleId) ||
            !skill.Skill.RoleId.StartsWith("developer-", StringComparison.Ordinal) ||
            !IsProductId(skill.Skill.Domain) || !IsProductId(skill.Skill.SkillId) ||
            skill.Skill.Revision < 1 || !IsText(skill.Name, 200) ||
            !IsText(skill.Prompt, MaximumAgentSkillPromptLength))
        {
            throw new InvalidDataException("The captured developer skill identity, revision, name, or prompt is invalid.");
        }
        ValidateDeveloperSkillDraft(draft);

        return new DeveloperSkillAnalysis(
            skill.Skill.RoleId,
            skill.Skill.Domain,
            skill.Skill.SkillId,
            skill.Name,
            skill.Skill.Revision,
            draft.Summary,
            draft.ProposedChanges,
            draft.Risks,
            draft.VerificationNeeds,
            draft.OpenQuestions);
    }

    /// <summary>Checks generated analysis before attaching trusted skill metadata.</summary>
    public static void ValidateDeveloperSkillDraft(DeveloperSkillDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!IsText(draft.Summary, 4_000))
        {
            throw new InvalidDataException("Developer analysis summary must contain 1 to 4000 non-whitespace characters.");
        }
        if (!IsBoundedTextList(draft.ProposedChanges, 1, 30, 2_000))
        {
            throw new InvalidDataException("Developer analysis proposedChanges must contain 1 to 30 nonempty strings, each at most 2000 characters.");
        }
        if (!IsBoundedTextList(draft.Risks, 0, 30, 1_000))
        {
            throw new InvalidDataException("Developer analysis risks must be an array of 0 to 30 nonempty strings, each at most 1000 characters.");
        }
        if (!IsBoundedTextList(draft.VerificationNeeds, 0, 30, 1_000))
        {
            throw new InvalidDataException("Developer analysis verificationNeeds must be an array of 0 to 30 nonempty strings, each at most 1000 characters.");
        }
        if (!IsBoundedTextList(draft.OpenQuestions, 0, 30, 1_000))
        {
            throw new InvalidDataException("Developer analysis openQuestions must be an array of 0 to 30 nonempty strings, each at most 1000 characters.");
        }
    }

    /// <summary>Maximum accepted feature-text length.</summary>
    public const int MaximumTextLength = 16_000;

    /// <summary>Maximum accepted rejection reason or revision feedback length.</summary>
    public const int MaximumCommentLength = 4_000;

    /// <summary>Maximum characters in one generated implementation file.</summary>
    public const int MaximumCodeChangeFileLength = 24_000;

    /// <summary>Maximum files in one generated implementation patch.</summary>
    public const int MaximumCodeChangeFiles = 32;

    /// <summary>Maximum characters in the complete generated implementation patch.</summary>
    public const int MaximumCodeChangeContentLength = 160_000;

    /// <summary>Bounds orchestration history; further changes require a new feature request.</summary>
    public const int MaximumPlanRevisions = 5;

    /// <summary>Maximum developers who can be selected to review one feature.</summary>
    public const int MaximumReviewerCount = 10;

    /// <summary>Maximum length of a stored AI skill prompt.</summary>
    public const int MaximumAgentSkillPromptLength = 12_000;

    /// <summary>Maximum length of a stored business analyst skill prompt.</summary>
    public const int MaximumBusinessAnalystSkillPromptLength = MaximumAgentSkillPromptLength;

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
        if (decision.ActorId is not null && !IsText(decision.ActorId, 128))
        {
            error = "Developer actor IDs must contain at most 128 characters.";
            return false;
        }
        return true;
    }

    /// <summary>Validates an analyst decision and its revision-bound feedback.</summary>
    public static bool IsValidBusinessAnalystDecision(BusinessAnalystDecision? decision, out string error)
    {
        error = string.Empty;
        if (decision is null || decision.Revision < 1 || !BusinessAnalystDecisionKinds.IsKnown(decision.Decision))
        {
            error = "A revision of at least 1 and a decision of approve, reject, or revise are required.";
            return false;
        }
        if (decision.Decision != BusinessAnalystDecisionKinds.Approve &&
            !IsText(decision.Comment, MaximumCommentLength))
        {
            if (decision.Decision != BusinessAnalystDecisionKinds.Revise ||
                decision.Answers is not { Length: > 0 and <= 30 } ||
                decision.Answers.Any(answer => answer is null ||
                    !IsText(answer.Question, 1_000) || !IsText(answer.Answer, MaximumCommentLength)))
            {
                error = $"A reason or complete set of question answers is required to {decision.Decision}.";
                return false;
            }
        }
        if (decision.Answers is { } answers &&
            (answers.Length > 30 || answers.Any(answer => answer is null ||
                !IsText(answer.Question, 1_000) || !IsText(answer.Answer, MaximumCommentLength)) ||
             answers.Select(answer => answer.Question.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != answers.Length))
        {
            error = "Question answers must have unique, nonempty questions and bounded nonempty answers.";
            return false;
        }
        if (decision.Comment is { Length: > MaximumCommentLength })
        {
            error = $"Comments are limited to {MaximumCommentLength} characters.";
            return false;
        }
        if (decision.ActorId is not null && !IsText(decision.ActorId, 128))
        {
            error = "Business analyst actor IDs must contain at most 128 characters.";
            return false;
        }
        return true;
    }

    /// <summary>Validates bounded architecture output and ensures its evidence cites inspected files.</summary>
    public static void ValidateArchitectureAnalysis(
        ArchitectureAnalysis analysis, IReadOnlySet<string> inspectedPaths)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(inspectedPaths);
        if (!IsText(analysis.Summary, 4_000) ||
            analysis.ComponentImpacts is not { Length: <= 30 } ||
            analysis.Options is not { Length: <= 10 } ||
            analysis.Risks is not { Length: <= 30 } ||
            analysis.OpenQuestions is not { Length: <= 30 } ||
            analysis.AdrProposals is not { Length: <= 20 } ||
            analysis.Risks.Any(risk => !IsText(risk, 2_000)) ||
            analysis.OpenQuestions.Any(question => !IsText(question, 1_000)))
        {
            throw new InvalidDataException("Architecture analysis exceeds its content limits or contains empty required text.");
        }

        foreach (var impact in analysis.ComponentImpacts)
        {
            if (impact is null || !IsText(impact.Component, 160) ||
                !IsText(impact.Impact, 2_000) ||
                impact.Evidence is not { Length: > 0 and <= 20 })
            {
                throw new InvalidDataException("Every architecture impact must identify a component, impact, and evidence.");
            }

            foreach (var evidence in impact.Evidence)
            {
                if (evidence is null || !IsText(evidence.Path, 240) ||
                    !inspectedPaths.Contains(evidence.Path) ||
                    !IsText(evidence.Claim, 1_000) ||
                    (evidence.Symbol is not null && !IsText(evidence.Symbol, 160)) ||
                    (evidence.StartLine is not null && evidence.StartLine < 1) ||
                    (evidence.EndLine is not null && evidence.EndLine < 1) ||
                    (evidence.StartLine is not null && evidence.EndLine is not null &&
                     evidence.EndLine < evidence.StartLine))
                {
                    throw new InvalidDataException("Architecture evidence must cite inspected files and valid source locations.");
                }
            }
        }

        foreach (var option in analysis.Options)
        {
            if (option is null || !IsText(option.Name, 160) ||
                !IsText(option.Description, 2_000) ||
                !IsBoundedTextList(option.Benefits, 0, 10, 1_000) ||
                !IsBoundedTextList(option.Tradeoffs, 0, 10, 1_000))
            {
                throw new InvalidDataException("Architecture options must include bounded names, descriptions, benefits, and trade-offs.");
            }
        }

        foreach (var proposal in analysis.AdrProposals)
        {
            if (proposal is null || !IsText(proposal.Title, 200) ||
                !IsText(proposal.Context, 2_000) ||
                !IsBoundedTextList(proposal.Options, 2, 10, 1_000) ||
                !IsText(proposal.Recommendation, 1_000) ||
                !IsText(proposal.Rationale, 2_000))
            {
                throw new InvalidDataException("Architecture decision proposals must describe context, options, and rationale.");
            }
        }
    }

    /// <summary>Validates an architecture decision and its revision-bound feedback.</summary>
    public static bool IsValidArchitectureAnalysisDecision(
        ArchitectureAnalysisDecision? decision, out string error)
    {
        error = string.Empty;
        if (decision is null || decision.Revision < 1 ||
            !ArchitectureAnalysisDecisionKinds.IsKnown(decision.Decision))
        {
            error = "A positive revision and decision of approve, revise, or reject are required.";
            return false;
        }
        if (decision.Decision != ArchitectureAnalysisDecisionKinds.Approve &&
            !IsText(decision.Comment, MaximumCommentLength))
        {
            error = $"A reason or feedback comment of at most {MaximumCommentLength} characters is required.";
            return false;
        }
        if (decision.Comment is { Length: > MaximumCommentLength } ||
            (decision.ActorId is not null && !IsText(decision.ActorId, 128)))
        {
            error = "The comment or actor ID exceeds its allowed length.";
            return false;
        }
        return true;
    }

    /// <summary>Checks a nonempty, bounded text value.</summary>
    public static bool IsText(string? text, int maximum = MaximumTextLength) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum;

    /// <summary>Validates a configured developer and the identifier used to address that developer.</summary>
    public static bool IsValidDeveloper(string? userId, DeveloperConfiguration? developer, out string error)
    {
        error = string.Empty;
        if (!IsText(userId, 128) || developer is null || !IsText(developer.DisplayName, 200) ||
            (developer.ReviewFocus is { } focus && focus.Length > 1_000) ||
            developer.Roles is not { Length: > 0 and <= 10 } ||
            developer.Roles.Any(role => !IsText(role, 80)) ||
            developer.Roles.Select(role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != developer.Roles.Length)
        {
            error = "A developer ID, display name, and 1-10 unique roles are required; review focus is limited to 1,000 characters.";
            return false;
        }

        return true;
    }

    /// <summary>Validates an analyst skill identifier and its editable prompt.</summary>
    public static bool IsValidBusinessAnalystSkill(
        string? analystId, string? skillId, BusinessAnalystSkillConfiguration? skill, out string error)
    {
        error = string.Empty;
        if (!IsText(analystId, 128) || !IsProductId(skillId) || skill is null ||
            !IsText(skill.Name, 200) ||
            !IsText(skill.Prompt, MaximumBusinessAnalystSkillPromptLength))
        {
            error = $"Analyst ID, lowercase skill ID, name, and a prompt of at most {MaximumBusinessAnalystSkillPromptLength} characters are required.";
            return false;
        }

        return true;
    }

    /// <summary>Validates the identifiers and content for a new immutable skill revision.</summary>
    public static bool IsValidAgentSkillRevision(
        string? roleId, string? domain, string? skillId,
        AgentSkillRevisionRequest? revision, out string error)
    {
        error = string.Empty;
        if (!IsProductId(roleId) || !IsProductId(domain) || !IsProductId(skillId) ||
            revision is null || !IsText(revision.Name, 200) ||
            !IsText(revision.Prompt, MaximumAgentSkillPromptLength) ||
            (revision.ChangeReason is { Length: > MaximumCommentLength }) ||
            (revision.ActorId is not null && !IsText(revision.ActorId, 128)))
        {
            error = $"Role, domain, and skill IDs must be lowercase identifiers; a name and prompt of at most {MaximumAgentSkillPromptLength} characters are required.";
            return false;
        }

        return true;
    }

    /// <summary>Validates a nonempty, bounded developer review response.</summary>
    public static bool IsValidReviewAssignmentResponse(ReviewAssignmentResponse? response, out string error)
    {
        error = string.Empty;
        if (response is null || !DeveloperReviewDecisionKinds.IsKnown(response.Decision) ||
            (response.Response is { } comment && comment.Length > MaximumCommentLength) ||
            (response.Decision == DeveloperReviewDecisionKinds.Revise && !IsText(response.Response, MaximumCommentLength)))
        {
            error = $"A known approve/revise decision is required; revise needs feedback and comments are limited to {MaximumCommentLength} characters.";
            return false;
        }

        return true;
    }

    /// <summary>Validates the requested reviewer count for one feature submission.</summary>
    public static bool IsValidFeatureSubmission(FeatureSubmission? submission, out string error)
    {
        error = string.Empty;
        if (submission is null || !IsText(submission.Text) ||
            !IsText(submission.BusinessAnalystId, 128) ||
            !IsProductId(submission.BusinessAnalystSkillId) ||
            !IsValidReviewerCount(submission.ReviewerCount))
        {
            error = $"Feature text, valid analyst and skill IDs, and a reviewer count from 1 to {MaximumReviewerCount} are required.";
            return false;
        }

        return true;
    }

    /// <summary>Validates the requested reviewer count for one feature submission.</summary>
    public static bool IsValidReviewerCount(int count) => count is >= 1 and <= MaximumReviewerCount;

    /// <summary>Checks a safe, lowercase product identifier.</summary>
    public static bool IsProductId(string? id) =>
        id is { Length: > 0 and <= 80 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Accepts HTTPS, SSH (with an optional username), or local file URLs without embedded secrets.</summary>
    public static bool IsRepositoryUrl(string? value)
    {
        if (value is not { Length: > 0 and <= 2048 } ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            value.Any(char.IsControl) ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.UserInfo.Length > 0 &&
                (uri.Scheme != "ssh" || Uri.UnescapeDataString(uri.UserInfo).Contains(':'))))
        {
            return false;
        }

        return uri.Scheme is "https" or "ssh" && !string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Scheme == "file" && !uri.IsUnc && !string.IsNullOrWhiteSpace(uri.LocalPath);
    }

    /// <summary>Accepts a short Git branch name, or null to use the repository's default branch.</summary>
    public static bool IsRepositoryBaseBranch(string? value) =>
        value is null ||
        value is { Length: > 0 and <= 255 } &&
        value is not ("HEAD" or "@") &&
        !value.StartsWith('-') &&
        !value.StartsWith("refs/", StringComparison.Ordinal) &&
        !value.EndsWith('.') &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("@{", StringComparison.Ordinal) &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^:?*[\\".Contains(c)) &&
        value.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') &&
            !part.EndsWith(".lock", StringComparison.Ordinal));

    /// <summary>Checks whether a repository URL points to a local file source.</summary>
    public static bool IsLocalFileRepositoryUrl(string? value) =>
        IsRepositoryUrl(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeFile &&
        !uri.IsUnc &&
        !string.IsNullOrWhiteSpace(uri.LocalPath);

    /// <summary>Validates catalog output and ensures all cited paths were actually inspected.</summary>
    public static void ValidateRepositoryCatalog(
        RepositoryFunctionality[] functionalities, IReadOnlySet<string> inspectedPaths)
    {
        ArgumentNullException.ThrowIfNull(functionalities);
        ArgumentNullException.ThrowIfNull(inspectedPaths);
        if (functionalities is not { Length: > 0 and <= 50 })
        {
            throw new InvalidDataException("Repository analysis must return between 1 and 50 functionality entries.");
        }

        foreach (var functionality in functionalities)
        {
            if (!IsText(functionality.Name, 120) ||
                !IsText(functionality.Description, 2_000) ||
                functionality.Paths is not { Length: > 0 and <= 10 } ||
                functionality.Paths.Any(path => !inspectedPaths.Contains(path)) ||
                functionality.Symbols is null ||
                functionality.Symbols.Length > 20 ||
                functionality.Symbols.Any(symbol => !IsText(symbol, 160)))
            {
                throw new InvalidDataException(
                    "Repository analysis returned an invalid functionality entry or cited an uninspected path.");
            }
        }
    }

    /// <summary>
    /// Validates generated paths and operations against the exact pinned Git tree.
    /// Only regular source/documentation files may be written to the Git index.
    /// </summary>
    public static void ValidateCodeChange(
        CodeChangeProposal proposal, IReadOnlyDictionary<string, string> trackedFileModes)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(trackedFileModes);
        if (!IsText(proposal.Summary, 2_000) ||
            proposal.Files is not { Length: > 0 and <= MaximumCodeChangeFiles })
        {
            throw new InvalidDataException("The implementation patch must include a bounded summary and at least one file change.");
        }

        var changedPaths = new HashSet<string>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var file in proposal.Files)
        {
            if (file is null || !IsSafeCodeChangePath(file.Path) || !changedPaths.Add(file.Path))
            {
                throw new InvalidDataException("The implementation patch contains an invalid or duplicate file path.");
            }

            var tracked = trackedFileModes.TryGetValue(file.Path, out var mode);
            var regularFile = mode is "100644" or "100755";
            switch (file.Operation)
            {
                case CodeFileOperations.Add when tracked:
                    throw new InvalidDataException($"The implementation patch cannot add existing path '{file.Path}'.");
                case CodeFileOperations.Update or CodeFileOperations.Delete when !tracked || !regularFile:
                    throw new InvalidDataException($"The implementation patch can only update or delete tracked regular file '{file.Path}'.");
                case CodeFileOperations.Add or CodeFileOperations.Update:
                    if (file.Content is null || file.Content.Contains('\0'))
                    {
                        throw new InvalidDataException($"The implementation patch must provide text content for '{file.Path}'.");
                    }
                    totalLength += file.Content.Length;
                    if (file.Content.Length > MaximumCodeChangeFileLength ||
                        totalLength > MaximumCodeChangeContentLength)
                    {
                        throw new InvalidDataException("The implementation patch exceeds its file or total content limit.");
                    }
                    break;
                case CodeFileOperations.Delete when file.Content is not null:
                    throw new InvalidDataException($"A delete operation must not include content for '{file.Path}'.");
                case CodeFileOperations.Add or CodeFileOperations.Update or CodeFileOperations.Delete:
                    break;
                default:
                    throw new InvalidDataException($"The implementation patch uses an unsupported operation for '{file.Path}'.");
            }
        }
    }

    private static bool IsSafeCodeChangePath(string? path)
    {
        if (path is not { Length: > 0 and <= 240 } ||
            path.StartsWith('/') ||
            path.Contains('\\') ||
            path.Any(char.IsControl))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.StartsWith('.') ||
                segment.Contains(':')) ||
            segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("vendor", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var name = segments[^1];
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetExtension(name).ToLowerInvariant() is
            ".cs" or ".csproj" or ".css" or ".go" or ".html" or ".java" or ".js" or ".jsx" or
            ".md" or ".py" or ".rs" or ".sql" or ".ts" or ".tsx";
    }

    /// <summary>Rejects incomplete model results rather than treating them as success.</summary>
    public static void ValidatePlan(DraftPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsText(plan.Summary))
        {
            throw new InvalidDataException("Draft plan summary must contain nonempty text.");
        }
        if (plan.Steps is not { Length: > 0 and <= 50 })
        {
            throw new InvalidDataException("Draft plan must contain between 1 and 50 steps.");
        }
        if (plan.Steps.Any(step => !IsText(step)))
        {
            throw new InvalidDataException("Every draft plan step must contain nonempty text.");
        }
        if (plan.OpenQuestions is null || plan.OpenQuestions.Length > 50 ||
            plan.OpenQuestions.Any(question => !IsText(question)))
        {
            throw new InvalidDataException("Draft plan openQuestions must be a list of at most 50 nonempty questions; use an empty list when none remain.");
        }
    }

    /// <summary>Validates synthesis content and its references to the exact captured skill revisions.</summary>
    public static void ValidateCollaborationSynthesis(
        CollaborationSynthesis synthesis,
        IReadOnlySet<AgentSkillRevisionReference> capturedSkills)
    {
        ArgumentNullException.ThrowIfNull(synthesis);
        ArgumentNullException.ThrowIfNull(capturedSkills);
        if (!IsText(synthesis.Summary, 4_000) ||
            synthesis.Agreements is not { Length: <= 30 } ||
            synthesis.Conflicts is not { Length: <= 30 } ||
            synthesis.UnresolvedQuestions is not { Length: <= 30 })
        {
            throw new InvalidDataException(
                "Collaboration synthesis summary must be nonempty and at most 4000 characters; agreements, conflicts, and unresolvedQuestions must each be arrays of at most 30 entries.");
        }

        static bool ValidSources(
            AgentSkillRevisionReference[]? sources,
            IReadOnlySet<AgentSkillRevisionReference> captured,
            int minimum) =>
            sources is { Length: <= 50 } &&
            sources.Length >= minimum &&
            sources.Distinct().Count() == sources.Length &&
            sources.All(captured.Contains);

        if (synthesis.Agreements.Any(finding =>
                finding is null || !IsText(finding.Finding, 2_000) ||
                !ValidSources(finding.Sources, capturedSkills, 2)) ||
            synthesis.Conflicts.Any(finding =>
                finding is null || !IsText(finding.Finding, 2_000) ||
                !ValidSources(finding.Sources, capturedSkills, 2)) ||
            synthesis.UnresolvedQuestions.Any(question =>
                question is null || !IsText(question.Question, 2_000) ||
                !ValidSources(question.Sources, capturedSkills, 0)))
        {
            throw new InvalidDataException(
                "Agreements and conflicts must cite at least two captured skill revisions; questions may cite captured sources or none.");
        }
    }

    /// <summary>Rejects incomplete or unbounded formal feature specifications.</summary>
    public static void ValidateFeatureSpecification(FeatureSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!IsText(specification.Title, 200))
        {
            throw new InvalidDataException("Feature specification title must be nonempty and at most 200 characters.");
        }
        if (!IsText(specification.ProblemStatement, 4_000))
        {
            throw new InvalidDataException("Feature specification problemStatement must be nonempty and at most 4000 characters.");
        }
        if (!IsBoundedTextList(specification.UserStories, 1, 30, 1_000))
        {
            throw new InvalidDataException("Feature specification userStories must contain 1 to 30 nonempty entries of at most 1000 characters each.");
        }
        if (!IsBoundedTextList(specification.AcceptanceCriteria, 1, 50, 1_000))
        {
            throw new InvalidDataException("Feature specification acceptanceCriteria must contain 1 to 50 nonempty entries of at most 1000 characters each.");
        }
        if (!IsBoundedTextList(specification.Constraints, 0, 30, 1_000))
        {
            throw new InvalidDataException("Feature specification constraints must contain 0 to 30 nonempty entries of at most 1000 characters each.");
        }
        if (!IsBoundedTextList(specification.OutOfScope, 0, 30, 1_000))
        {
            throw new InvalidDataException("Feature specification outOfScope must contain 0 to 30 nonempty entries of at most 1000 characters each.");
        }
        if (!IsBoundedTextList(specification.OpenQuestions, 0, 30, 1_000))
        {
            throw new InvalidDataException("Feature specification openQuestions must contain 0 to 30 nonempty entries of at most 1000 characters each.");
        }
    }

    private static bool IsBoundedTextList(string[]? values, int minimum, int maximum, int itemLength) =>
        values is not null && values.Length >= minimum && values.Length <= maximum &&
        values.All(value => IsText(value, itemLength));
}
