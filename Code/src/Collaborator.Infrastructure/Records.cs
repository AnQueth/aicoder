using Collaborator.Core;

namespace Collaborator.Infrastructure;

/// <summary>Registered product scope for the initial product-brief pilot.</summary>
public sealed class Product
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Brief { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? RepositoryBaseBranch { get; set; }
    public string? RepositoryCatalogCommit { get; set; }
    public string? RepositoryCatalogJson { get; set; }
    public DateTimeOffset? RepositoryCatalogUpdatedAt { get; set; }
}

/// <summary>Persisted feature intent and durable workflow identity.</summary>
public sealed class FeatureRun
{
    public Guid Id { get; set; }
    public required string ProductId { get; set; }
    public required string Text { get; set; }
    public required string ProductBrief { get; set; }
    public required string IdempotencyKey { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? RepositoryBaseBranch { get; set; }
    public string? RepositoryCommit { get; set; }
    public string? RepositoryCatalogJson { get; set; }
    public required string BusinessAnalystId { get; set; }
    public required string BusinessAnalystSkillId { get; set; }
    public required string BusinessAnalystSkillName { get; set; }
    public int BusinessAnalystSkillRevision { get; set; }
    public required string BusinessAnalystPrompt { get; set; }
    public int FeatureSpecificationRevision { get; set; }
    public bool BusinessAnalystApproved { get; set; }
    public int ReviewerCount { get; set; } = 3;
    public string? SelectedReviewersJson { get; set; }
    public string DeveloperSkillsJson { get; set; } = "[]";
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PlanJson { get; set; }
    public int PlanRevision { get; set; }
    public string? Decision { get; set; }
    public string? DecisionComment { get; set; }
    public string? DecisionBy { get; set; }
    public string? FailureCode { get; set; }
}

/// <summary>Stable identity and active revision pointer for an AI role/domain skill.</summary>
public sealed class AgentSkillRecord
{
    public required string RoleId { get; set; }
    public required string SkillId { get; set; }
    public required string Domain { get; set; }
    public int ActiveRevision { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Immutable authored prompt revision for an AI skill.</summary>
public sealed class AgentSkillRevisionRecord
{
    public required string RoleId { get; set; }
    public required string SkillId { get; set; }
    public required string Domain { get; set; }
    public int Revision { get; set; }
    public required string Name { get; set; }
    public required string Prompt { get; set; }
    public string? CreatedBy { get; set; }
    public string? ChangeReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One revisioned stage result with its input and skill provenance.</summary>
public sealed class WorkflowArtifactRecord
{
    public Guid RunId { get; set; }
    public required string Stage { get; set; }
    public required string ArtifactKey { get; set; }
    public int Revision { get; set; }
    public required string ArtifactType { get; set; }
    public required string PayloadJson { get; set; }
    public string InputArtifactRevisionsJson { get; set; } = "[]";
    public string SkillRevisionsJson { get; set; } = "[]";
    public string? RepositoryCommit { get; set; }
    public string Status { get; set; } = WorkflowArtifactStatuses.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Decision { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionComment { get; set; }
    public DateTimeOffset? StaleAt { get; set; }
    public string? StaleReason { get; set; }
}

/// <summary>A question and optional human answer attached to one exact artifact revision.</summary>
public sealed class StageQuestionRecord
{
    public Guid RunId { get; set; }
    public required string Stage { get; set; }
    public required string ArtifactKey { get; set; }
    public int Revision { get; set; }
    public int Index { get; set; }
    public required string Question { get; set; }
    public string? Answer { get; set; }
    public string? AnsweredBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnsweredAt { get; set; }
}

/// <summary>A human review bound to one exact stage artifact revision and reviewer.</summary>
public sealed class StageArtifactReviewRecord
{
    public Guid RunId { get; set; }
    public required string Stage { get; set; }
    public required string ArtifactKey { get; set; }
    public int Revision { get; set; }
    public required string ReviewerId { get; set; }
    public required string Decision { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Formal feature specification and business decision for one revision.</summary>
public sealed class FeatureSpecificationRevisionRecord
{
    public Guid RunId { get; set; }
    public int Revision { get; set; }
    public required string SpecificationJson { get; set; }
    public string? AnswersJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Decision { get; set; }
    public string? Comment { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Immutable plan revision with the human decision recorded against it.</summary>
public sealed class PlanRevisionRecord
{
    public Guid RunId { get; set; }
    public int Revision { get; set; }
    public required string PlanJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Decision { get; set; }
    public string? Comment { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Developer settings used to create plan-review assignments.</summary>
public sealed class DeveloperRecord
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public string? ReviewFocus { get; set; }
    public string RolesJson { get; set; } = "[]";
    public bool IsEnabled { get; set; } = true;
}

/// <summary>One developer's review prompt and response for a specific plan revision.</summary>
public sealed class ReviewAssignmentRecord
{
    public Guid RunId { get; set; }
    public int Revision { get; set; }
    public required string DeveloperId { get; set; }
    public required string DeveloperDisplayName { get; set; }
    public string? ReviewFocus { get; set; }
    public string MatchedRolesJson { get; set; } = "[]";
    public required string ReviewPrompt { get; set; }
    public string? Decision { get; set; }
    public string? Response { get; set; }
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RespondedAt { get; set; }
}

/// <summary>Generated patch and published branch metadata for one approved feature run.</summary>
public sealed class CodeChangeRecord
{
    public Guid RunId { get; set; }
    public string Status { get; set; } = CodeChangeStatuses.PatchGenerated;
    public string? Summary { get; set; }
    public string? PatchJson { get; set; }
    public string? ChangedFilesJson { get; set; }
    public string? BranchName { get; set; }
    public string? BranchCommit { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Durable business-to-workflow delivery intent.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public required string Kind { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? FailureCode { get; set; }
}

/// <summary>Attributable workflow progression without sensitive prompt payloads.</summary>
public sealed class RunEvent
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public required string Kind { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
