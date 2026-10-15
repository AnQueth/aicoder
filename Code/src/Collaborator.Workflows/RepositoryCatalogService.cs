using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Collaborator.Workflows;

/// <summary>Opens a run's pinned Git source, generates a validated catalog, and pins it to the run.</summary>
public sealed class RepositoryCatalogService(
    CollaboratorDb db,
    IRepositoryCatalogGenerator generator,
    ILogger<RepositoryCatalogService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Returns the run-pinned catalog or builds and persists one for the source commit.</summary>
    public async Task<RepositoryCatalog> GetForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        if (run.RepositoryCatalogJson is not null)
        {
            return JsonSerializer.Deserialize<RepositoryCatalog>(run.RepositoryCatalogJson, Json)
                ?? throw new InvalidDataException("The persisted repository catalog is invalid.");
        }

        if (!InputRules.IsRepositoryUrl(run.RepositoryUrl))
        {
            throw new InvalidOperationException(
                "An HTTPS, SSH, or local file Git repository is required before a plan can be generated.");
        }

        await using var snapshot = await ReadRepositoryAsync(
            run.RepositoryUrl!, run.RepositoryCommit, run.RepositoryBaseBranch, cancellationToken);
        var product = await db.Products.SingleAsync(x => x.Id == run.ProductId, cancellationToken);
        RepositoryCatalog catalog;
        var reused = false;
        var cachedCatalog = product.RepositoryCatalogJson is { } cachedJson
            ? JsonSerializer.Deserialize<RepositoryCatalog>(cachedJson, Json)
                ?? throw new InvalidDataException("The cached repository catalog is invalid.")
            : null;
        if (product.RepositoryCatalogCommit == snapshot.Commit &&
            cachedCatalog is { FormatVersion: RepositoryCatalog.CurrentFormatVersion })
        {
            reused = true;
            catalog = cachedCatalog;
        }
        else
        {
            var analysisInput = JsonSerializer.Serialize(new
            {
                repositoryCommit = snapshot.Commit,
                repositorySource = snapshot.Source
            }, Json);
            var functionalities = await generator.GenerateCatalogAsync(analysisInput, cancellationToken);
            var inspectedPaths = snapshot.Source.Paths.ToHashSet(StringComparer.Ordinal);
            InputRules.ValidateRepositoryCatalog(functionalities, inspectedPaths);
            catalog = new RepositoryCatalog(
                snapshot.Commit,
                DateTimeOffset.UtcNow,
                snapshot.Source.Paths.Length,
                functionalities,
                RepositoryCatalog.CurrentFormatVersion);

            product.RepositoryCatalogCommit = catalog.Commit;
            product.RepositoryCatalogJson = JsonSerializer.Serialize(catalog, Json);
            product.RepositoryCatalogUpdatedAt = catalog.GeneratedAt;
            db.Events.Add(new RunEvent
            {
                RunId = runId,
                Kind = "repository_catalog_generated",
                Actor = "copilot"
            });
        }
        if (reused)
        {
            db.Events.Add(new RunEvent
            {
                RunId = runId,
                Kind = "repository_catalog_reused",
                Actor = "workflow"
            });
        }

        run.RepositoryCommit = catalog.Commit;
        run.RepositoryCatalogJson = JsonSerializer.Serialize(catalog, Json);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Pinned repository catalog for run {RunId} to commit {Commit} with {FunctionalityCount} entries.",
            runId, catalog.Commit, catalog.Functionalities.Length);
        return catalog;
    }

    /// <summary>Opens the tracked text source at a run's pinned commit for on-demand reads.</summary>
    internal async Task<RepositorySnapshot> ReadForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var snapshot = await TryReadForRunAsync(runId, cancellationToken);
        return snapshot ?? throw new InvalidOperationException(
            "A repository URL and pinned commit are required to generate code.");
    }

    /// <summary>Opens and pins a repository snapshot when one is configured for the run.</summary>
    internal async Task<RepositorySnapshot?> TryReadForRunAsync(
        Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        if (run.RepositoryUrl is null)
        {
            return null;
        }

        var snapshot = await ReadRepositoryAsync(
            run.RepositoryUrl, run.RepositoryCommit, run.RepositoryBaseBranch, cancellationToken);
        try
        {
            if (run.RepositoryCommit is null)
            {
                run.RepositoryCommit = snapshot.Commit;
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (!string.Equals(snapshot.Commit, run.RepositoryCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The repository snapshot does not match the run-pinned commit.");
            }
            return snapshot;
        }
        catch
        {
            await snapshot.DisposeAsync();
            throw;
        }
    }

    private static async Task<RepositorySnapshot> ReadRepositoryAsync(
        string repositoryUrl, string? expectedCommit, string? baseBranch, CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"collaborator-repo-{Guid.NewGuid():N}");
        var clonePath = Path.Combine(temporaryRoot, "source");
        Directory.CreateDirectory(temporaryRoot);
        var keepClone = false;
        try
        {
            var commit = await DownloadRepositoryAsync(
                clonePath, repositoryUrl, expectedCommit, baseBranch, cancellationToken);

            var tree = await RunGitAsync(clonePath, ["ls-tree", "-r", "-z", commit], cancellationToken);
            var treeFiles = ParseTree(tree);
            var trackedFileModes = treeFiles.ToDictionary(
                file => file.Path, file => file.Mode, StringComparer.Ordinal);
            var paths = treeFiles
                .Where(file => file.Type == "blob" &&
                    (file.Mode is "100644" or "100755") &&
                    RepositorySourceTools.IsEligibleSourcePath(file.Path))
                .Select(file => file.Path)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                throw new InvalidDataException("The Git repository contains no eligible tracked text files.");
            }
            keepClone = true;
            return new RepositorySnapshot(
                commit,
                new RepositorySourceReference(clonePath, commit, paths),
                trackedFileModes,
                temporaryRoot);
        }
        finally
        {
            if (!keepClone && Directory.Exists(temporaryRoot))
            {
                DeleteTemporaryDirectory(temporaryRoot);
            }
        }
    }

    private static RepositoryTreeFile[] ParseTree(string tree)
    {
        var files = new List<RepositoryTreeFile>();
        foreach (var entry in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\t');
            if (separator < 0)
            {
                throw new InvalidDataException("Git returned a malformed repository tree entry.");
            }
            var metadata = entry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3)
            {
                throw new InvalidDataException("Git returned a malformed repository tree entry.");
            }
            files.Add(new RepositoryTreeFile(metadata[0], metadata[1], entry[(separator + 1)..]));
        }
        return files.ToArray();
    }

    private static bool IsCommitId(string commit) =>
        commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit);

    internal static async Task<string> DownloadRepositoryAsync(
        string clonePath, string repositoryUrl, string? expectedCommit, string? baseBranch,
        CancellationToken cancellationToken)
    {
        if (!InputRules.IsRepositoryUrl(repositoryUrl) ||
            !InputRules.IsRepositoryBaseBranch(baseBranch))
        {
            throw new InvalidDataException("The repository URL or base branch is invalid.");
        }
        if (expectedCommit is not null && !IsCommitId(expectedCommit))
        {
            throw new InvalidDataException("The persisted repository commit identifier is invalid.");
        }

        Directory.CreateDirectory(clonePath);
        await RunGitAsync(clonePath, ["init", "--quiet"], cancellationToken);
        await RunGitAsync(clonePath, ["remote", "add", "origin", repositoryUrl], cancellationToken);
        // Fetch the exact branch, not a same-named tag. Replays use only the pinned commit.
        var revision = expectedCommit ?? (baseBranch is null ? "HEAD" : $"refs/heads/{baseBranch}");
        await RunGitAsync(clonePath, [
            "fetch", "--quiet", "--no-tags", "--depth=1", "origin", revision
        ], cancellationToken);
        var commit = (await RunGitAsync(
            clonePath, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"], cancellationToken)).Trim();
        if (!IsCommitId(commit) ||
            expectedCommit is not null && !string.Equals(commit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Git did not fetch the requested repository commit.");
        }
        return commit;
    }

    internal static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null,
        byte[]? standardInputBytes = null)
    {
        if (standardInput is not null && standardInputBytes is not null)
        {
            throw new ArgumentException("Git input must be either text or bytes, not both.");
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null || standardInputBytes is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        if (standardInput is not null)
        {
            process.StartInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                process.StartInfo.Environment[name] = value;
            }
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";
        }
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the Git executable.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (standardInputBytes is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(standardInputBytes, timeout.Token);
                process.StandardInput.Close();
            }
            else if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git command '{string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: {error.Trim()}");
            }
            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Git repository inspection exceeded its 30-second command limit.");
        }
    }

    internal static void DeleteTemporaryDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        }
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: true);
    }

    internal sealed class RepositorySnapshot : IAsyncDisposable
    {
        private readonly string temporaryRoot;

        public RepositorySnapshot(
            string commit,
            RepositorySourceReference source,
            IReadOnlyDictionary<string, string> trackedFileModes,
            string temporaryRoot)
        {
            Commit = commit;
            Source = source;
            TrackedFileModes = trackedFileModes;
            this.temporaryRoot = temporaryRoot;
        }

        public string Commit { get; }
        public RepositorySourceReference Source { get; }
        public IReadOnlyDictionary<string, string> TrackedFileModes { get; }

        public async Task<string> CreateImplementationWorktreeAsync(
            string branchName, CancellationToken cancellationToken)
        {
            var worktreePath = Path.Combine(temporaryRoot, "implementation");
            await RunGitAsync(Source.GitDirectory, [
                "check-ref-format", "--branch", branchName
            ], cancellationToken);
            await RunGitAsync(Source.GitDirectory, [
                "worktree", "add", "-b", branchName, worktreePath, Commit
            ], cancellationToken);

            var checkedOutCommit = (await RunGitAsync(
                worktreePath, ["rev-parse", "HEAD"], cancellationToken)).Trim();
            if (!string.Equals(checkedOutCommit, Commit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The implementation worktree does not match the pinned repository commit.");
            }
            var checkedOutBranch = (await RunGitAsync(
                worktreePath, ["branch", "--show-current"], cancellationToken)).Trim();
            if (!string.Equals(checkedOutBranch, branchName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The implementation worktree is not on its assigned feature branch.");
            }
            return worktreePath;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(temporaryRoot))
            {
                DeleteTemporaryDirectory(temporaryRoot);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RepositoryTreeFile(string Mode, string Type, string Path);
}
