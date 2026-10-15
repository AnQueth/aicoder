using Collaborator.Core;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Collaborator.Agents;

/// <summary>Provides validated tools over eligible files at one pinned Git commit.</summary>
public sealed class RepositorySourceTools
{
    private const int MaximumSearchQueryLength = 200;
    private const int MaximumSearchResults = 30;
    private const int SearchBatchSize = 100;
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".aws", ".azure", ".ssh", "bin", "build", "dist",
        "node_modules", "obj", "packages", "vendor"
    };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".a", ".apk", ".avif", ".bmp", ".class", ".db", ".dll", ".doc",
        ".docx", ".dylib", ".eot", ".exe", ".gif", ".gz", ".ico", ".jar",
        ".jpeg", ".jpg", ".lib", ".mp3", ".mp4", ".o", ".obj", ".pdf", ".pdb",
        ".cer", ".crt", ".der", ".key", ".p12", ".p7b", ".p7c", ".p7s", ".pem",
        ".pfx", ".png", ".ppt", ".pptx", ".pyc", ".so", ".sqlite", ".tar",
        ".tif", ".tiff", ".ttf", ".wasm", ".webp", ".woff", ".woff2", ".xls",
        ".xlsx", ".zip"
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, string>? inMemoryFiles;
    private readonly RepositorySourceReference? source;
    private readonly string[] originalPaths;

    /// <summary>Initializes tools from evidence, primarily for offline providers and tests.</summary>
    public RepositorySourceTools(IEnumerable<RepositoryFileEvidence> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var materialized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in sourceFiles)
        {
            if (file is null || !IsEligibleSourcePath(file.Path) || file.Content is null ||
                file.Content.Contains('\0') || !materialized.TryAdd(file.Path, file.Content))
            {
                throw new InvalidDataException("Repository source evidence contains an invalid or duplicate file.");
            }
        }
        if (materialized.Count == 0)
        {
            throw new InvalidDataException("Repository source evidence is empty.");
        }

        inMemoryFiles = materialized;
        originalPaths = materialized.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Initializes tools that read files only when an agent asks for them.</summary>
    public RepositorySourceTools(RepositorySourceReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.GitDirectory) ||
            !Directory.Exists(source.GitDirectory) ||
            !IsCommitId(source.Commit) ||
            source.Paths is null ||
            source.Paths.Length == 0 ||
            source.Paths.Any(path => !IsEligibleSourcePath(path)) ||
            source.Paths.Distinct(StringComparer.Ordinal).Count() != source.Paths.Length)
        {
            throw new InvalidDataException("The pinned repository source reference is invalid.");
        }
        this.source = source;
        originalPaths = source.Paths.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Creates Copilot SDK declarations for read-only repository exploration.</summary>
    public ICollection<AIFunctionDeclaration> CreateTools()
    {
        var tools = new List<AIFunctionDeclaration>
        {
            CopilotTool.DefineTool(
                () => ListSourceFiles(),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions
                {
                    Name = "list_repository_source_files",
                    Description = "List eligible tracked text files in the pinned repository."
                }),
            CopilotTool.DefineTool(
                ([Description("Case-insensitive text to find in source lines; 1 to 200 characters.")] string query,
                    [Description("Maximum matching lines to return, from 1 to 30.")] int maxResults) =>
                    SearchSourceCode(query, maxResults),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions
                {
                    Name = "search_repository_source",
                    Description = "Search eligible tracked text files in the pinned repository."
                }),
            CopilotTool.DefineTool(
                ([Description("Case-insensitive .NET regular expression to match against each source line; 1 to 200 characters.")] string pattern,
                    [Description("Maximum matching lines to return, from 1 to 30.")] int maxResults) =>
                    SearchSourceCodeRegex(pattern, maxResults),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions
                {
                    Name = "search_repository_source_regex",
                    Description = "Search eligible tracked text files in the pinned repository with a .NET regular expression."
                }),
            CopilotTool.DefineTool(
                ([Description("Exact repository-relative path returned by the list or search tool.")] string path) =>
                    ReadSourceFile(path),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions
                {
                    Name = "read_repository_source_file",
                    Description = "Read the complete text of one eligible file at the pinned commit."
                })
        };

        return tools;
    }

    /// <summary>Returns eligible repository-relative paths in ordinal order.</summary>
    public string ListSourceFiles() => JsonSerializer.Serialize(GetCurrentPaths());

    /// <summary>Searches the snapshot and returns matching paths, line numbers, and complete matching lines.</summary>
    public string SearchSourceCode(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumSearchQueryLength)
        {
            throw new ArgumentException($"Search query must contain 1 to {MaximumSearchQueryLength} characters.", nameof(query));
        }
        if (maxResults is < 1 or > MaximumSearchResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), $"Search result limit must be between 1 and {MaximumSearchResults}.");
        }

        if (inMemoryFiles is not null)
        {
            var matches = new List<SourceSearchMatch>(Math.Min(maxResults, MaximumSearchResults));
            foreach (var path in GetCurrentPaths())
            {
                AddMatches(matches, path, ReadSourceFile(path), query, maxResults);
                if (matches.Count == maxResults)
                {
                    break;
                }
            }
            return JsonSerializer.Serialize(matches, Json);
        }

        var source = this.source
            ?? throw new InvalidOperationException("Repository source tools have no source.");
        return JsonSerializer.Serialize(SearchGitSnapshot(source, query, maxResults), Json);
    }

    /// <summary>Searches every current eligible source line using a bounded, case-insensitive .NET regex.</summary>
    public string SearchSourceCodeRegex(string pattern, int maxResults)
    {
        ValidateSearchLimit(pattern, maxResults);
        Regex regex;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexMatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The regular expression is invalid.", nameof(pattern), exception);
        }

        var matches = new List<SourceSearchMatch>(maxResults);
        try
        {
            foreach (var path in GetCurrentPaths())
            {
                AddRegexMatches(matches, path, ReadSourceFile(path), regex, maxResults);
                if (matches.Count == maxResults)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new TimeoutException(
                $"Repository regex search exceeded the {RegexMatchTimeout.TotalMilliseconds:0} ms per-line match limit.",
                exception);
        }

        return JsonSerializer.Serialize(matches, Json);
    }

    /// <summary>Returns the complete content of one exact eligible path in the pinned snapshot.</summary>
    public string ReadSourceFile(string path)
    {
        if (!IsEligibleSourcePath(path))
        {
            throw new KeyNotFoundException("The requested path is not an eligible file in this repository snapshot.");
        }

        if (Array.BinarySearch(originalPaths, path, StringComparer.Ordinal) < 0)
        {
            throw new KeyNotFoundException("The requested path is not an eligible file in this repository snapshot.");
        }

        return ReadOriginalSourceFile(path);
    }

    private string ReadOriginalSourceFile(string path)
    {
        var content = inMemoryFiles is not null
            ? inMemoryFiles[path]
            : ReadGitFile(source
                ?? throw new InvalidOperationException("Repository source tools have no source."), path);
        if (content.Contains('\0'))
        {
            throw new InvalidDataException("The requested repository file is binary, not text.");
        }
        return content;
    }

    /// <summary>Creates read-only repository tools and removes internal paths and source metadata from the prompt.</summary>
    public static RepositoryToolContext PrepareInput(string input)
    {
        var root = JsonNode.Parse(input)?.AsObject()
            ?? throw new InvalidDataException("Copilot provider input must be a JSON object.");
        string? workingDirectory = null;
        if (root.TryGetPropertyValue("implementationWorkspace", out var workspaceNode))
        {
            workingDirectory = workspaceNode?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new InvalidDataException("The implementation worktree path is invalid.");
            }
            root.Remove("implementationWorkspace");
        }
        if (root.TryGetPropertyValue("repositorySource", out var repositorySource))
        {
            root.Remove("repositorySource");
            if (repositorySource is not null)
            {
                var source = repositorySource.Deserialize<RepositorySourceReference>(Json)
                    ?? throw new InvalidDataException("The pinned repository source reference is invalid.");
                var sourceTools = new RepositorySourceTools(source);
                return new RepositoryToolContext(root.ToJsonString(Json), sourceTools, workingDirectory);
            }
        }

        if (!root.TryGetPropertyValue("sourceFiles", out var sourceEvidence) || sourceEvidence is null)
        {
            return new RepositoryToolContext(root.ToJsonString(Json), null, workingDirectory);
        }

        var files = sourceEvidence.Deserialize<RepositoryFileEvidence[]>(Json);
        if (files is null || files.Length == 0)
        {
            throw new InvalidDataException("Repository source evidence is empty or invalid.");
        }
        root.Remove("sourceFiles");
        var evidenceTools = new RepositorySourceTools(files);
        return new RepositoryToolContext(root.ToJsonString(Json), evidenceTools, workingDirectory);
    }

    private string[] GetCurrentPaths() => originalPaths;

    /// <summary>Checks paths while excluding secrets, generated output, and known binary file types.</summary>
    public static bool IsEligibleSourcePath(string? path)
    {
        if (path is not { Length: > 0 and <= 240 } ||
            path.StartsWith('/') ||
            path.Contains('\\') ||
            path.Any(char.IsControl))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.Contains(':') || ExcludedDirectories.Contains(segment)))
        {
            return false;
        }

        var fileName = segments[^1];
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("private-key", StringComparison.OrdinalIgnoreCase) ||
            fileName is ".npmrc" or ".pypirc" or ".netrc" or ".dockercfg" or
                "id_rsa" or "id_ed25519" or "credentials" or "credentials.json" or
                ".ds_store" or "thumbs.db" ||
            fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            BinaryExtensions.Contains(Path.GetExtension(fileName)))
        {
            return false;
        }
        return true;
    }

    private static void AddMatches(
        ICollection<SourceSearchMatch> matches, string path, string content, string query, int maxResults)
    {
        var lines = content.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            matches.Add(new SourceSearchMatch(path, index + 1, line));
            if (matches.Count == maxResults)
            {
                return;
            }
        }
    }

    private static void AddRegexMatches(
        ICollection<SourceSearchMatch> matches,
        string path,
        string content,
        Regex regex,
        int maxResults)
    {
        var lines = content.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!regex.IsMatch(line))
            {
                continue;
            }
            matches.Add(new SourceSearchMatch(path, index + 1, line));
            if (matches.Count == maxResults)
            {
                return;
            }
        }
    }

    private static void ValidateSearchLimit(string pattern, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaximumSearchQueryLength)
        {
            throw new ArgumentException(
                $"Search pattern must contain 1 to {MaximumSearchQueryLength} characters.",
                nameof(pattern));
        }
        if (maxResults is < 1 or > MaximumSearchResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                $"Search result limit must be between 1 and {MaximumSearchResults}.");
        }
    }

    private static List<SourceSearchMatch> SearchGitSnapshot(
        RepositorySourceReference source, string query, int maxResults, IEnumerable<string>? paths = null)
    {
        var matches = new List<SourceSearchMatch>(maxResults);
        foreach (var batch in (paths ?? source.Paths).Chunk(SearchBatchSize))
        {
            var arguments = new List<string>
            {
                "grep", "--no-color", "-n", "-I", "-i", "-F", "-m", "1",
                "-e", query, source.Commit, "--"
            };
            arguments.AddRange(batch.Select(path => $":(literal){path}"));
            var result = RunGit(source.GitDirectory, arguments);
            if (result.ExitCode is not (0 or 1))
            {
                throw new InvalidOperationException($"Git source search failed: {result.Error.Trim()}");
            }
            foreach (var rawLine in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.TrimEnd('\r');
                var pathSeparator = line.IndexOf(':');
                var numberSeparator = pathSeparator < 0 ? -1 : line.IndexOf(':', pathSeparator + 1);
                if (pathSeparator <= 0 || numberSeparator <= pathSeparator + 1 ||
                    !int.TryParse(line.AsSpan(pathSeparator + 1, numberSeparator - pathSeparator - 1),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var lineNumber))
                {
                    throw new InvalidDataException("Git returned a malformed source-search result.");
                }
                matches.Add(new SourceSearchMatch(
                    line[..pathSeparator], lineNumber, line[(numberSeparator + 1)..]));
                if (matches.Count == maxResults)
                {
                    return matches;
                }
            }
        }
        return matches;
    }

    private static string ReadGitFile(RepositorySourceReference source, string path)
    {
        var result = RunGit(source.GitDirectory, ["show", $"{source.Commit}:{path}"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git could not read '{path}': {result.Error.Trim()}");
        }
        return result.Output;
    }

    private static (int ExitCode, string Output, string Error) RunGit(
        string workingDirectory, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Git for repository source access.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WaitAll(outputTask, errorTask);
            throw new TimeoutException("Repository source search or file read exceeded two minutes.");
        }
        Task.WaitAll(outputTask, errorTask);
        return (process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static bool IsCommitId(string commit) =>
        commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit);

    private sealed record SourceSearchMatch(string Path, int Line, string Excerpt);
}

/// <summary>Prepared prompt, read-only repository tools, and optional implementation worktree.</summary>
public sealed record RepositoryToolContext(
    string Prompt, RepositorySourceTools? SourceTools, string? WorkingDirectory = null)
{
    public ICollection<AIFunctionDeclaration> Tools { get; } = SourceTools?.CreateTools() ?? [];
}
