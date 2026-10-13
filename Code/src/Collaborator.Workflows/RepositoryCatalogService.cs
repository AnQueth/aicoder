using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Collaborator.Workflows;

/// <summary>Inspects a run's local Git source, generates a validated catalog, and pins it to the run.</summary>
public sealed class RepositoryCatalogService(
    CollaboratorDb db,
    IRepositoryCatalogGenerator generator,
    ILogger<RepositoryCatalogService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".css", ".go", ".html", ".java", ".js", ".jsx",
        ".md", ".py", ".rs", ".sql", ".ts", ".tsx"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "build", "dist", "node_modules", "obj", "packages", "vendor"
    };

    /// <summary>Returns the run-pinned catalog or builds and persists one for the source commit.</summary>
    public async Task<RepositoryCatalog> GetForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        if (run.RepositoryCatalogJson is not null)
        {
            return JsonSerializer.Deserialize<RepositoryCatalog>(run.RepositoryCatalogJson, Json)
                ?? throw new InvalidDataException("The persisted repository catalog is invalid.");
        }

        if (!InputRules.IsLocalFileRepositoryUrl(run.RepositoryUrl))
        {
            throw new InvalidOperationException(
                "A local file:// Git repository is required before a plan can be generated.");
        }

        var snapshot = await ReadRepositoryAsync(run.RepositoryUrl!, run.RepositoryCommit, cancellationToken);
        var product = await db.Products.SingleAsync(x => x.Id == run.ProductId, cancellationToken);
        RepositoryCatalog catalog;
        var reused = false;
        if (product.RepositoryCatalogCommit == snapshot.Commit &&
            product.RepositoryCatalogJson is { } cachedJson)
        {
            reused = true;
            catalog = JsonSerializer.Deserialize<RepositoryCatalog>(cachedJson, Json)
                ?? throw new InvalidDataException("The cached repository catalog is invalid.");
        }
        else
        {
            var analysisInput = JsonSerializer.Serialize(new
            {
                repositoryCommit = snapshot.Commit,
                sourceFiles = snapshot.Files
            }, Json);
            var functionalities = await generator.GenerateCatalogAsync(analysisInput, cancellationToken);
            var inspectedPaths = snapshot.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            InputRules.ValidateRepositoryCatalog(functionalities, inspectedPaths);
            catalog = new RepositoryCatalog(
                snapshot.Commit, DateTimeOffset.UtcNow, snapshot.Files.Length, functionalities);

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

    /// <summary>Reads bounded text and the tracked-file inventory at a run's pinned commit.</summary>
    internal async Task<RepositorySnapshot> ReadForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var snapshot = await TryReadForRunAsync(runId, cancellationToken);
        return snapshot ?? throw new InvalidOperationException(
            "A repository URL and pinned commit are required to generate code.");
    }

    /// <summary>Reads and pins a local repository snapshot when one is configured for the run.</summary>
    internal async Task<RepositorySnapshot?> TryReadForRunAsync(
        Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        if (!InputRules.IsLocalFileRepositoryUrl(run.RepositoryUrl))
        {
            return null;
        }

        var snapshot = await ReadRepositoryAsync(run.RepositoryUrl!, run.RepositoryCommit, cancellationToken);
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

    private static async Task<RepositorySnapshot> ReadRepositoryAsync(
        string repositoryUrl, string? expectedCommit, CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"collaborator-repo-{Guid.NewGuid():N}");
        var clonePath = Path.Combine(temporaryRoot, "source");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await RunGitAsync(temporaryRoot, [
                "clone", "--quiet", "--no-checkout", "--depth", "1", "--no-tags", "--",
                repositoryUrl, clonePath
            ], cancellationToken);
            if (expectedCommit is not null)
            {
                if (!IsCommitId(expectedCommit))
                {
                    throw new InvalidDataException("The persisted repository commit identifier is invalid.");
                }
                await RunGitAsync(clonePath, [
                    "fetch", "--quiet", "--no-tags", "--depth=1", "origin", expectedCommit
                ], cancellationToken);
            }

            var revision = expectedCommit is null ? "HEAD" : "FETCH_HEAD";
            var commit = (await RunGitAsync(clonePath, ["rev-parse", revision], cancellationToken)).Trim();
            if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Git returned an invalid repository commit identifier.");
            }

            var tree = await RunGitAsync(clonePath, ["ls-tree", "-r", "-z", revision], cancellationToken);
            var treeFiles = ParseTree(tree);
            var trackedFileModes = treeFiles.ToDictionary(
                file => file.Path, file => file.Mode, StringComparer.Ordinal);
            var paths = treeFiles
                .Where(file => file.Type == "blob" &&
                    (file.Mode is "100644" or "100755") &&
                    IsIndexablePath(file.Path))
                .Select(file => file.Path)
                .ToArray();
            if (paths.Length == 0)
            {
                throw new InvalidDataException("The Git repository contains no supported source or documentation files.");
            }
            if (paths.Length > InputRules.MaximumRepositoryFiles)
            {
                throw new InvalidDataException(
                    $"Repository analysis is limited to {InputRules.MaximumRepositoryFiles} tracked source files; found {paths.Length}.");
            }

            var files = new List<RepositoryFileEvidence>(paths.Length);
            var totalLength = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objectPath = $"{revision}:{path}";
                var sizeText = (await RunGitAsync(clonePath, ["cat-file", "-s", objectPath], cancellationToken)).Trim();
                if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var byteLength))
                {
                    throw new InvalidDataException($"Git returned an invalid file size for '{path}'.");
                }
                if (byteLength > InputRules.MaximumRepositoryFileLength * 4L)
                {
                    throw new InvalidDataException(
                        $"Repository file '{path}' exceeds the {InputRules.MaximumRepositoryFileLength}-character analysis limit.");
                }

                var content = await RunGitAsync(clonePath, ["show", objectPath], cancellationToken);
                if (content.Contains('\0'))
                {
                    continue;
                }
                if (content.Length > InputRules.MaximumRepositoryFileLength)
                {
                    throw new InvalidDataException(
                        $"Repository file '{path}' exceeds the {InputRules.MaximumRepositoryFileLength}-character analysis limit.");
                }
                totalLength += content.Length;
                if (totalLength > InputRules.MaximumRepositoryContentLength)
                {
                    throw new InvalidDataException(
                        $"Repository analysis is limited to {InputRules.MaximumRepositoryContentLength} total source characters.");
                }
                files.Add(new RepositoryFileEvidence(path, content));
            }

            if (files.Count == 0)
            {
                throw new InvalidDataException("No readable text files were available for repository analysis.");
            }
            return new RepositorySnapshot(commit, files.ToArray(), trackedFileModes);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
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

    private static bool IsIndexablePath(string path)
    {
        var segments = path.Split('/');
        if (segments.Any(segment => ExcludedDirectories.Contains(segment)) ||
            segments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)))
        {
            return false;
        }

        var fileName = segments[^1];
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return SourceExtensions.Contains(Path.GetExtension(fileName));
    }

    internal static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null,
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
            if (standardInput is not null)
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

    internal sealed record RepositorySnapshot(
        string Commit,
        RepositoryFileEvidence[] Files,
        IReadOnlyDictionary<string, string> TrackedFileModes);

    private sealed record RepositoryTreeFile(string Mode, string Type, string Path);
}
