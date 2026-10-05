namespace Collaborator.Core;

/// <summary>A product with a reviewed brief and an optional source repository.</summary>
public sealed record ProductRegistration(string Id, string Name, string Brief, string? RepositoryUrl = null);

/// <summary>Changes the optional Git repository configured for a product.</summary>
public sealed record RepositorySourceUpdate(string? RepositoryUrl);

/// <summary>The requested feature intent, not an approved specification.</summary>
public sealed record FeatureSubmission(string Text);

/// <summary>
/// A human decision bound to an exact plan revision. <see cref="Decision"/> is one of
/// <see cref="PlanDecisionKinds"/>; <see cref="Comment"/> records the rejection reason or revision feedback.
/// </summary>
public sealed record PlanDecision(int Revision, string Decision, string? Comment = null);

/// <summary>A configured developer who receives plan review assignments.</summary>
public sealed record DeveloperConfiguration(string DisplayName, string? ReviewFocus = null, bool IsEnabled = true);

/// <summary>A developer's response to one plan review assignment.</summary>
public sealed record ReviewAssignmentResponse(string Response);

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

/// <summary>A source file included as untrusted evidence for repository analysis.</summary>
public sealed record RepositoryFileEvidence(string Path, string Content);

/// <summary>A repository capability with locations that can be verified against inspected files.</summary>
public sealed record RepositoryFunctionality(string Name, string Description, string[] Paths, string[] Symbols);

/// <summary>A validated repository catalog pinned to one Git commit.</summary>
public sealed record RepositoryCatalog(
    string Commit,
    DateTimeOffset GeneratedAt,
    int FilesAnalyzed,
    RepositoryFunctionality[] Functionalities);

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

/// <summary>A structured draft plan produced by the initial compatibility workflow.</summary>
public sealed record DraftPlan(string Summary, string[] Steps, string[] OpenQuestions);

/// <summary>Shared input validation independent of HTTP and model providers.</summary>
public static class InputRules
{
    /// <summary>Maximum accepted feature-text length.</summary>
    public const int MaximumTextLength = 16_000;

    /// <summary>Maximum accepted rejection reason or revision feedback length.</summary>
    public const int MaximumCommentLength = 4_000;

    /// <summary>Maximum source files included in one repository analysis.</summary>
    public const int MaximumRepositoryFiles = 64;

    /// <summary>Maximum characters included from one tracked source file.</summary>
    public const int MaximumRepositoryFileLength = 12_000;

    /// <summary>Maximum total source characters included in one repository analysis.</summary>
    public const int MaximumRepositoryContentLength = 160_000;

    /// <summary>Maximum files in one generated implementation patch.</summary>
    public const int MaximumCodeChangeFiles = 32;

    /// <summary>Maximum characters in the complete generated implementation patch.</summary>
    public const int MaximumCodeChangeContentLength = 160_000;

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

    /// <summary>Validates a configured developer and the identifier used to address that developer.</summary>
    public static bool IsValidDeveloper(string? userId, DeveloperConfiguration? developer, out string error)
    {
        error = string.Empty;
        if (!IsText(userId, 128) || developer is null || !IsText(developer.DisplayName, 200) ||
            (developer.ReviewFocus is { } focus && focus.Length > 1_000))
        {
            error = "A developer ID and display name are required; review focus is limited to 1,000 characters.";
            return false;
        }

        return true;
    }

    /// <summary>Validates a nonempty, bounded developer review response.</summary>
    public static bool IsValidReviewAssignmentResponse(ReviewAssignmentResponse? response, out string error)
    {
        error = string.Empty;
        if (response is null || !IsText(response.Response, MaximumCommentLength))
        {
            error = $"A review response of at most {MaximumCommentLength} characters is required.";
            return false;
        }

        return true;
    }

    /// <summary>Checks a safe, lowercase product identifier.</summary>
    public static bool IsProductId(string? id) =>
        id is { Length: > 0 and <= 80 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Accepts credential-free HTTPS, SSH, or local file repository URLs.</summary>
    public static bool IsRepositoryUrl(string? value)
    {
        if (value is not { Length: > 0 and <= 2048 } ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        return uri.Scheme is "https" or "ssh" or "file" &&
            (uri.Scheme == "file" || !string.IsNullOrWhiteSpace(uri.Host));
    }

    /// <summary>Accepts only credential-free local file URLs for repository inspection.</summary>
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
                    if (file.Content.Length > MaximumRepositoryFileLength * 2 ||
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
        if (!IsText(plan.Summary) || plan.Steps is not { Length: > 0 and <= 50 } ||
            plan.Steps.Any(step => !IsText(step)) || plan.OpenQuestions is null ||
            plan.OpenQuestions.Length > 50 || plan.OpenQuestions.Any(question => !IsText(question)))
        {
            throw new InvalidDataException("Agent output does not satisfy the draft-plan contract.");
        }
    }
}
