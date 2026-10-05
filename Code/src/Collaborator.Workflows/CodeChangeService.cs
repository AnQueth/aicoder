using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace Collaborator.Workflows;

/// <summary>Creates a bounded Copilot patch and publishes it to an isolated review branch.</summary>
public sealed class CodeChangeService(
    CollaboratorDb db,
    ICodeChangeGenerator generator,
    RepositoryCatalogService repositoryCatalog,
    ILogger<CodeChangeService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Generates once and durably saves a validated patch for later publication.</summary>
    public async Task GenerateAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        EnsureApproved(run);

        var existing = await db.CodeChanges.SingleOrDefaultAsync(x => x.RunId == runId, cancellationToken);
        if (existing?.PatchJson is not null)
        {
            return;
        }

        var revision = await db.PlanRevisions.AsNoTracking()
            .SingleAsync(x => x.RunId == runId && x.Revision == run.PlanRevision, cancellationToken);
        if (revision.Decision != PlanDecisionKinds.Approve)
        {
            throw new InvalidOperationException("Code generation requires an approved plan revision.");
        }
        var plan = JsonSerializer.Deserialize<DraftPlan>(revision.PlanJson, Json)
            ?? throw new InvalidDataException("The approved plan is invalid.");
        var catalog = run.RepositoryCatalogJson is null
            ? throw new InvalidDataException("Repository analysis is required before code generation.")
            : JsonSerializer.Deserialize<RepositoryCatalog>(run.RepositoryCatalogJson, Json)
                ?? throw new InvalidDataException("The pinned repository catalog is invalid.");
        var snapshot = await repositoryCatalog.ReadForRunAsync(runId, cancellationToken);
        var input = JsonSerializer.Serialize(new
        {
            productBrief = run.ProductBrief,
            featureRequest = run.Text,
            approvedPlan = plan,
            repositoryCommit = snapshot.Commit,
            repositoryCatalog = catalog,
            trackedFiles = snapshot.TrackedFileModes.Keys.Order(StringComparer.Ordinal),
            sourceFiles = snapshot.Files
        }, Json);

        var proposal = await generator.GenerateCodeChangeAsync(input, cancellationToken);
        InputRules.ValidateCodeChange(proposal, snapshot.TrackedFileModes);
        var changedFiles = proposal.Files.Select(file => file.Path).ToArray();
        if (existing is null)
        {
            existing = new CodeChangeRecord { RunId = runId };
            db.CodeChanges.Add(existing);
        }
        existing.Status = CodeChangeStatuses.PatchGenerated;
        existing.Summary = proposal.Summary.Trim();
        existing.PatchJson = JsonSerializer.Serialize(proposal, Json);
        existing.ChangedFilesJson = JsonSerializer.Serialize(changedFiles, Json);
        db.Events.Add(new RunEvent { RunId = runId, Kind = "code_change_generated", Actor = "copilot" });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Validated an implementation patch for run {RunId} with {ChangedFileCount} files.",
            runId, changedFiles.Length);
    }

    /// <summary>Creates the proposed commit in a temporary bare-index clone without checking files out.</summary>
    public async Task PrepareBranchAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == runId, cancellationToken);
        EnsureApproved(run);
        var change = await db.CodeChanges.SingleAsync(x => x.RunId == runId, cancellationToken);
        if (change.BranchCommit is not null)
        {
            return;
        }
        if (change.Status != CodeChangeStatuses.PatchGenerated || change.PatchJson is null)
        {
            throw new InvalidOperationException("A generated patch is required before preparing a branch.");
        }
        if (!InputRules.IsLocalFileRepositoryUrl(run.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(run.RepositoryCommit))
        {
            throw new InvalidOperationException("A local file repository and pinned commit are required to publish a review branch.");
        }

        var snapshot = await repositoryCatalog.ReadForRunAsync(runId, cancellationToken);
        var proposal = JsonSerializer.Deserialize<CodeChangeProposal>(change.PatchJson, Json)
            ?? throw new InvalidDataException("The persisted implementation patch is invalid.");
        InputRules.ValidateCodeChange(proposal, snapshot.TrackedFileModes);
        var (branchName, commit) = await GitReviewBranch.PrepareAsync(
            run.RepositoryUrl!, run.RepositoryCommit!, runId, change.CreatedAt,
            proposal, snapshot.TrackedFileModes, cancellationToken);

        change.BranchName = branchName;
        change.BranchCommit = commit;
        change.Status = CodeChangeStatuses.BranchPrepared;
        db.Events.Add(new RunEvent { RunId = runId, Kind = "review_branch_prepared", Actor = "workflow" });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Pushes only the prepared commit to its unique human-review branch.</summary>
    public async Task PublishBranchAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        EnsureApproved(run);
        var change = await db.CodeChanges.SingleAsync(x => x.RunId == runId, cancellationToken);
        if (change.Status == CodeChangeStatuses.Ready)
        {
            return;
        }
        if (change.Status != CodeChangeStatuses.BranchPrepared ||
            string.IsNullOrWhiteSpace(change.BranchName) ||
            string.IsNullOrWhiteSpace(change.BranchCommit) ||
            !InputRules.IsLocalFileRepositoryUrl(run.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(run.RepositoryCommit) ||
            string.IsNullOrWhiteSpace(change.PatchJson))
        {
            throw new InvalidOperationException("A prepared review branch and local repository are required before publication.");
        }

        var snapshot = await repositoryCatalog.ReadForRunAsync(runId, cancellationToken);
        var proposal = JsonSerializer.Deserialize<CodeChangeProposal>(change.PatchJson, Json)
            ?? throw new InvalidDataException("The persisted implementation patch is invalid.");
        InputRules.ValidateCodeChange(proposal, snapshot.TrackedFileModes);
        await GitReviewBranch.PublishAsync(
            run.RepositoryUrl!,
            run.RepositoryCommit,
            runId,
            change.CreatedAt,
            proposal,
            snapshot.TrackedFileModes,
            change.BranchName,
            change.BranchCommit,
            cancellationToken);
        change.Status = CodeChangeStatuses.Ready;
        run.Status = "code_change_ready";
        db.Events.Add(new RunEvent { RunId = runId, Kind = "review_branch_published", Actor = "workflow" });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Published review branch {BranchName} for run {RunId} at commit {Commit}.",
            change.BranchName, runId, change.BranchCommit);
    }

    private static void EnsureApproved(FeatureRun run)
    {
        if (run.Decision != PlanDecisionKinds.Approve || run.Approved is not true)
        {
            throw new InvalidOperationException("Code generation is allowed only after an explicit plan approval.");
        }
    }
}

internal static class GitReviewBranch
{
    public static async Task<(string BranchName, string Commit)> PrepareAsync(
        string repositoryUrl,
        string baseCommit,
        Guid runId,
        DateTimeOffset patchCreatedAt,
        CodeChangeProposal proposal,
        IReadOnlyDictionary<string, string> trackedFileModes,
        CancellationToken cancellationToken)
    {
        var branchName = $"collaborator/feature-{runId:N}";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"collaborator-change-{Guid.NewGuid():N}");
        var clonePath = Path.Combine(temporaryRoot, "source");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await CloneAtCommitAsync(temporaryRoot, clonePath, repositoryUrl, baseCommit, cancellationToken);
            var commit = await CreateCommitAsync(
                clonePath, baseCommit, runId, patchCreatedAt, proposal, trackedFileModes, cancellationToken);
            return (branchName, commit);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                RepositoryCatalogService.DeleteTemporaryDirectory(temporaryRoot);
            }
        }
    }

    public static async Task PublishAsync(
        string repositoryUrl,
        string baseCommit,
        Guid runId,
        DateTimeOffset patchCreatedAt,
        CodeChangeProposal proposal,
        IReadOnlyDictionary<string, string> trackedFileModes,
        string branchName,
        string expectedCommit,
        CancellationToken cancellationToken)
    {
        var refName = $"refs/heads/{branchName}";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"collaborator-publish-{Guid.NewGuid():N}");
        var clonePath = Path.Combine(temporaryRoot, "source");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await CloneAtCommitAsync(temporaryRoot, clonePath, repositoryUrl, baseCommit, cancellationToken);
            var actualCommit = await CreateCommitAsync(
                clonePath, baseCommit, runId, patchCreatedAt, proposal, trackedFileModes, cancellationToken);
            if (!string.Equals(actualCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The review-branch commit could not be reproduced from its persisted patch.");
            }

            var existing = (await RepositoryCatalogService.RunGitAsync(
                clonePath, ["ls-remote", "--heads", repositoryUrl, refName], cancellationToken)).Trim();
            if (existing.Length > 0)
            {
                var remoteCommit = existing.Split('\t', StringSplitOptions.RemoveEmptyEntries)[0];
                if (string.Equals(remoteCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                throw new InvalidOperationException(
                    "The review branch already exists with a different commit; refusing to overwrite it.");
            }

            try
            {
                await RepositoryCatalogService.RunGitAsync(clonePath, [
                    "push", "--quiet", repositoryUrl, $"{expectedCommit}:{refName}"
                ], cancellationToken);
            }
            catch (InvalidOperationException)
            {
                var afterFailure = (await RepositoryCatalogService.RunGitAsync(
                    clonePath, ["ls-remote", "--heads", repositoryUrl, refName], cancellationToken)).Trim();
                var remoteCommit = afterFailure.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.Equals(remoteCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                RepositoryCatalogService.DeleteTemporaryDirectory(temporaryRoot);
            }
        }
    }

    private static async Task CloneAtCommitAsync(
        string temporaryRoot,
        string clonePath,
        string repositoryUrl,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        await RepositoryCatalogService.RunGitAsync(temporaryRoot, [
            "clone", "--quiet", "--no-checkout", "--depth", "1", "--no-tags", "--",
            repositoryUrl, clonePath
        ], cancellationToken);
        await RepositoryCatalogService.RunGitAsync(clonePath, [
            "fetch", "--quiet", "--no-tags", "--depth=1", "origin", baseCommit
        ], cancellationToken);
        var fetchedCommit = (await RepositoryCatalogService.RunGitAsync(
            clonePath, ["rev-parse", "FETCH_HEAD"], cancellationToken)).Trim();
        if (!string.Equals(fetchedCommit, baseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Git did not fetch the approved plan's pinned commit.");
        }
    }

    private static async Task<string> CreateCommitAsync(
        string clonePath,
        string baseCommit,
        Guid runId,
        DateTimeOffset patchCreatedAt,
        CodeChangeProposal proposal,
        IReadOnlyDictionary<string, string> trackedFileModes,
        CancellationToken cancellationToken)
    {
        await RepositoryCatalogService.RunGitAsync(clonePath, ["read-tree", baseCommit], cancellationToken);
        foreach (var file in proposal.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Operation == CodeFileOperations.Delete)
            {
                await RepositoryCatalogService.RunGitAsync(clonePath, [
                    "update-index", "--force-remove", "--", file.Path
                ], cancellationToken);
                continue;
            }

            var blob = (await RepositoryCatalogService.RunGitAsync(
                clonePath, ["hash-object", "-w", "--stdin"], cancellationToken, file.Content)).Trim();
            if (blob.Length is not (40 or 64) || !blob.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"Git returned an invalid object identifier for '{file.Path}'.");
            }
            var mode = trackedFileModes.TryGetValue(file.Path, out var oldMode) && oldMode == "100755"
                ? "100755"
                : "100644";
            await RepositoryCatalogService.RunGitAsync(clonePath, [
                "update-index", "-z", "--index-info"
            ], cancellationToken, $"{mode} {blob}\t{file.Path}\0");
        }

        var tree = (await RepositoryCatalogService.RunGitAsync(
            clonePath, ["write-tree"], cancellationToken)).Trim();
        var commitDate = patchCreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var commit = (await RepositoryCatalogService.RunGitAsync(clonePath, [
            "-c", "user.name=Collaborator",
            "-c", "user.email=collaborator@localhost",
            "commit-tree", tree, "-p", baseCommit,
            "-m", $"Implement approved feature request {runId:D}"
        ], cancellationToken, environment: new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = commitDate,
            ["GIT_COMMITTER_DATE"] = commitDate
        })).Trim();
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Git returned an invalid review-branch commit identifier.");
        }
        return commit;
    }
}
