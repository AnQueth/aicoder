using Collaborator.Core;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Collaborator.Agents;

/// <summary>Provides bounded, read-only tools over source files from one pinned repository snapshot.</summary>
public sealed class RepositorySourceTools
{
    private const int MaximumSearchQueryLength = 200;
    private const int MaximumSearchResults = 30;
    private const int MaximumExcerptLength = 300;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".css", ".go", ".html", ".java", ".js", ".jsx",
        ".md", ".py", ".rs", ".sql", ".ts", ".tsx"
    };
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "build", "dist", "node_modules", "obj", "packages", "vendor"
    };
    private readonly IReadOnlyDictionary<string, string> files;

    /// <summary>Initializes tools from validated repository evidence.</summary>
    /// <param name="sourceFiles">Eligible files read from the run's pinned Git commit.</param>
    /// <exception cref="InvalidDataException">The evidence exceeds configured bounds or has unsafe paths.</exception>
    public RepositorySourceTools(IEnumerable<RepositoryFileEvidence> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var materialized = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var file in sourceFiles)
        {
            if (file is null || !IsSafeSnapshotPath(file.Path) || file.Content is null ||
                file.Content.Contains('\0') || file.Content.Length > InputRules.MaximumRepositoryFileLength ||
                !materialized.TryAdd(file.Path, file.Content))
            {
                throw new InvalidDataException("Repository source evidence contains an invalid or duplicate file.");
            }
            totalLength += file.Content.Length;
            if (materialized.Count > InputRules.MaximumRepositoryFiles ||
                totalLength > InputRules.MaximumRepositoryContentLength)
            {
                throw new InvalidDataException("Repository source evidence exceeds the configured analysis limits.");
            }
        }
        if (materialized.Count == 0)
        {
            throw new InvalidDataException("Repository source evidence is empty.");
        }

        files = materialized;
    }

    /// <summary>Creates Copilot SDK declarations for the snapshot's read-only operations.</summary>
    /// <returns>The list, search, and exact-file-read functions available to the model.</returns>
    public ICollection<AIFunctionDeclaration> CreateTools() =>
    [
        CopilotTool.DefineTool(
            () => ListSourceFiles(),
            toolOptions: new CopilotToolOptions { SkipPermission = true },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = "list_repository_source_files",
                Description = "List eligible source and documentation paths in this run's pinned Git snapshot."
            }),
        CopilotTool.DefineTool(
            ([Description("Case-insensitive text to find in source lines; 1 to 200 characters.")] string query,
                [Description("Maximum matches to return, from 1 to 30.")] int maxResults) =>
                SearchSourceCode(query, maxResults),
            toolOptions: new CopilotToolOptions { SkipPermission = true },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = "search_repository_source",
                Description = "Search eligible source text in this run's pinned Git snapshot."
            }),
        CopilotTool.DefineTool(
            ([Description("Exact repository-relative path returned by the list or search tool.")] string path) =>
                ReadSourceFile(path),
            toolOptions: new CopilotToolOptions { SkipPermission = true },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = "read_repository_source_file",
                Description = "Read one exact eligible source file from this run's pinned Git snapshot."
            })
    ];

    /// <summary>Returns the eligible repository-relative paths in ordinal order.</summary>
    public string ListSourceFiles() => JsonSerializer.Serialize(files.Keys.Order(StringComparer.Ordinal));

    /// <summary>Searches source lines and returns bounded path, line-number, and excerpt matches.</summary>
    /// <param name="query">Case-insensitive text to search for.</param>
    /// <param name="maxResults">Maximum number of matches to return, between 1 and 30.</param>
    /// <returns>JSON containing matching paths, one-based line numbers, and bounded excerpts.</returns>
    /// <exception cref="ArgumentException">The query is empty or exceeds the supported length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The result limit is outside the supported range.</exception>
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

        var matches = new List<SourceSearchMatch>(Math.Min(maxResults, MaximumSearchResults));
        foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var lines = content.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd('\r');
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                matches.Add(new SourceSearchMatch(
                    path,
                    index + 1,
                    line.Length <= MaximumExcerptLength ? line : line[..MaximumExcerptLength]));
                if (matches.Count == maxResults)
                {
                    return JsonSerializer.Serialize(matches, JsonSerializerOptions.Web);
                }
            }
        }

        return JsonSerializer.Serialize(matches, JsonSerializerOptions.Web);
    }

    /// <summary>Returns content for one exact eligible path in the pinned snapshot.</summary>
    /// <param name="path">Exact repository-relative file path.</param>
    /// <returns>The file's bounded text content.</returns>
    /// <exception cref="KeyNotFoundException">The path is not present in this snapshot.</exception>
    public string ReadSourceFile(string path)
    {
        if (!IsSafeSnapshotPath(path) || !files.TryGetValue(path, out var content))
        {
            throw new KeyNotFoundException("The requested path is not an eligible file in this repository snapshot.");
        }
        return content;
    }

    /// <summary>Creates tools from source evidence and removes the evidence from the prompt payload.</summary>
    /// <param name="input">Serialized provider input, optionally containing sourceFiles.</param>
    /// <returns>A prompt without raw source text and tools bound to the supplied snapshot.</returns>
    public static RepositoryToolContext PrepareInput(string input)
    {
        var root = JsonNode.Parse(input)?.AsObject()
            ?? throw new InvalidDataException("Copilot provider input must be a JSON object.");
        if (!root.TryGetPropertyValue("sourceFiles", out var sourceEvidence) || sourceEvidence is null)
        {
            return new RepositoryToolContext(input, []);
        }

        var files = sourceEvidence.Deserialize<RepositoryFileEvidence[]>(JsonSerializerOptions.Web);
        if (files is null || files.Length == 0)
        {
            throw new InvalidDataException("Repository source evidence is empty or invalid.");
        }
        root.Remove("sourceFiles");
        return new RepositoryToolContext(
            root.ToJsonString(JsonSerializerOptions.Web),
            new RepositorySourceTools(files).CreateTools());
    }

    private static bool IsSafeSnapshotPath(string? path)
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
            segment.StartsWith(".", StringComparison.Ordinal) || segment.Contains(':') ||
            ExcludedDirectories.Contains(segment)))
        {
            return false;
        }

        var fileName = segments[^1];
        return !fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
            !fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            SourceExtensions.Contains(Path.GetExtension(fileName));
    }

    private sealed record SourceSearchMatch(string Path, int Line, string Excerpt);
}

/// <summary>Prepared prompt and source-reading tools for one Copilot session.</summary>
/// <param name="Prompt">Serialized provider input with raw repository source removed.</param>
/// <param name="Tools">Read-only source tools bound to the removed evidence.</param>
public sealed record RepositoryToolContext(string Prompt, ICollection<AIFunctionDeclaration> Tools);
