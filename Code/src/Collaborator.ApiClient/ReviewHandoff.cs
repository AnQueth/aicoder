using System.Text;
using Collaborator.Core;

namespace Collaborator.ApiClient;

internal static class ReviewHandoff
{
    public static string Format(
        Guid runId, string? repositoryUrl, string? baseCommit, CodeChangeStatus change,
        string checkoutDirectory)
    {
        var output = new StringBuilder();
        output.AppendLine();
        output.AppendLine("=== Review generated code ===");
        output.AppendLine($"  run             : {runId}");
        output.AppendLine($"  repository      : {repositoryUrl ?? "(not provided by API)"}");
        output.AppendLine($"  review branch   : {change.BranchName ?? "(not provided by API)"}");
        output.AppendLine($"  review commit   : {change.Commit ?? "(not provided by API)"}");
        output.AppendLine($"  base commit     : {baseCommit ?? "(not provided by API)"}");
        output.AppendLine("  Changes were published for review only; nothing was merged.");
        output.AppendLine("  The temporary implementation worktree was removed.");
        output.AppendLine("  This client has not created a local review checkout.");
        output.AppendLine("  Changed files (relative to the repository root):");
        foreach (var file in change.ChangedFiles)
        {
            output.AppendLine($"    {file}");
        }

        if (!InputRules.IsRepositoryUrl(repositoryUrl) ||
            string.IsNullOrWhiteSpace(repositoryUrl) ||
            string.IsNullOrWhiteSpace(change.BranchName) ||
            string.IsNullOrWhiteSpace(change.Commit))
        {
            output.AppendLine(
                "  Review checkout commands unavailable: the API must supply a credential-free repository URL, branch, and commit.");
            output.AppendLine("  Update/restart the API and reconnect with --run to retrieve the review information.");
            return output.ToString();
        }

        output.AppendLine();
        output.AppendLine("  Run these PowerShell commands to create and open a NEW review folder:");
        output.AppendLine("  Use an unused destination; do not clone over an existing working folder.");
        output.AppendLine($"  Review folder after cloning: {checkoutDirectory}");
        output.AppendLine($"    git clone --single-branch --branch {Quote(change.BranchName)} -- {Quote(repositoryUrl)} {Quote(checkoutDirectory)}");
        output.AppendLine("  If cloning fails, stop here and resolve the Git error before continuing.");
        output.AppendLine($"    git -C {Quote(checkoutDirectory)} rev-parse HEAD");
        output.AppendLine($"  Expected HEAD: {change.Commit}. If it differs, stop and check the review branch.");
        if (!string.IsNullOrWhiteSpace(baseCommit))
        {
            output.AppendLine($"    git -C {Quote(checkoutDirectory)} diff --stat {Quote(baseCommit)} {Quote(change.Commit)} --");
            output.AppendLine($"    git -C {Quote(checkoutDirectory)} diff {Quote(baseCommit)} {Quote(change.Commit)} --");
        }
        output.AppendLine($"    code {Quote(checkoutDirectory)}");
        output.AppendLine("  Review the diff and run the repository's documented build/tests before merging.");
        output.AppendLine("  Workflow success means publication succeeded, not that build/tests passed.");
        return output.ToString();
    }

    // PowerShell single-quoted arguments escape a quote by doubling it.
    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
